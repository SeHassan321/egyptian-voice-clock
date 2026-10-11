using Android.Content;
using Android.Media;
using VoiceClock.Core.Audio;
using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.Playback;
using AEncoding = Android.Media.Encoding;

namespace VoiceClock.App.Droid;

/// <summary>
/// Plays a composed announcement as one continuous PCM stream through <see cref="AudioTrack"/>:
/// no gaps or clicks between words, transient audio focus (other apps duck), nothing played during
/// a call, and success reported only after the player has actually played the buffer.
/// </summary>
public sealed class AndroidAudioOutput : IAudioOutput
{
    private static readonly TimeSpan StallLimit = TimeSpan.FromMilliseconds(1500);
    private readonly IAppLog _log;

    public AndroidAudioOutput(IAppLog log)
    {
        _log = log;
    }

    public async Task<PlaybackOutcome> PlayAsync(PcmAudio audio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.IsEmpty)
        {
            return new PlaybackOutcome(PlaybackOutcomeKind.Failed, "Nothing to play.");
        }

        if (Android.App.Application.Context.GetSystemService(Context.AudioService) is not AudioManager audioManager)
        {
            return new PlaybackOutcome(PlaybackOutcomeKind.Failed, "Audio service unavailable.");
        }

        if (audioManager.Mode is Android.Media.Mode.InCall or Android.Media.Mode.InCommunication or Android.Media.Mode.Ringtone)
        {
            return new PlaybackOutcome(PlaybackOutcomeKind.BlockedByCall);
        }

        var attributes = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Speech)!
            .Build()!;

        var focusLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var focusListener = new FocusListener(focusLost);
        var focusRequest = new AudioFocusRequestClass.Builder(AudioFocus.GainTransientMayDuck)
            .SetAudioAttributes(attributes)!
            .SetOnAudioFocusChangeListener(focusListener)!
            .Build()!;

        if (audioManager.RequestAudioFocus(focusRequest) != AudioFocusRequest.Granted)
        {
            return new PlaybackOutcome(PlaybackOutcomeKind.FocusDenied);
        }

        AudioTrack? track = null;
        try
        {
            var format = new AudioFormat.Builder()
                .SetSampleRate(audio.SampleRate)!
                .SetEncoding(AEncoding.Pcm16bit)!
                .SetChannelMask(ChannelOut.Mono)!
                .Build()!;

            var minBuffer = AudioTrack.GetMinBufferSize(audio.SampleRate, ChannelOut.Mono, AEncoding.Pcm16bit);
            if (minBuffer <= 0)
            {
                return new PlaybackOutcome(PlaybackOutcomeKind.Failed, $"Unsupported output format ({minBuffer}).");
            }

            track = new AudioTrack.Builder()
                .SetAudioAttributes(attributes)!
                .SetAudioFormat(format)!
                .SetBufferSizeInBytes(Math.Max(minBuffer * 2, audio.SampleRate / 5 * 2))!
                .SetTransferMode(AudioTrackMode.Stream)!
                .Build()!;

            if (track.State != AudioTrackState.Initialized)
            {
                return new PlaybackOutcome(PlaybackOutcomeKind.Failed, "Audio output could not be initialized.");
            }

            track.Play();
            var samples = audio.Samples;
            var total = samples.Length;
            var writeError = await Task.Run(() => WriteAll(track, samples, focusLost.Task, cancellationToken), cancellationToken);
            if (writeError is not null)
            {
                return new PlaybackOutcome(PlaybackOutcomeKind.Failed, writeError);
            }

            if (focusLost.Task.IsCompleted)
            {
                return new PlaybackOutcome(PlaybackOutcomeKind.Interrupted);
            }

            // Stop() in streaming mode lets everything already written finish playing.
            track.Stop();
            return await WaitUntilPlayedAsync(track, total, audio, focusLost.Task, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return PlaybackOutcome.Cancelled;
        }
        catch (Java.Lang.Exception ex)
        {
            _log.Warn("Audio playback failed.", ex);
            return new PlaybackOutcome(PlaybackOutcomeKind.Failed, ex.Message);
        }
        finally
        {
            if (track is not null)
            {
                try
                {
                    if (track.PlayState != PlayState.Stopped)
                    {
                        track.Pause();
                        track.Flush();
                        track.Stop();
                    }
                }
                catch (Java.Lang.IllegalStateException)
                {
                }

                track.Release();
                track.Dispose();
            }

            audioManager.AbandonAudioFocusRequest(focusRequest);
        }
    }

    private static string? WriteAll(AudioTrack track, short[] samples, Task focusLost, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (focusLost.IsCompleted)
            {
                return null;
            }

            var written = track.Write(samples, offset, Math.Min(4096, samples.Length - offset));
            if (written < 0)
            {
                return $"AudioTrack.write failed ({written}).";
            }

            offset += written;
        }

        return null;
    }

    private static async Task<PlaybackOutcome> WaitUntilPlayedAsync(AudioTrack track, int total, PcmAudio audio, Task focusLost, CancellationToken cancellationToken)
    {
        // Some devices hold back the final fraction of a buffer; the composed audio always ends in
        // silence, so stopping within this margin of the end never cuts speech.
        var silentTailFrames = audio.SampleRate / 10;
        var deadline = DateTime.UtcNow + audio.Duration + TimeSpan.FromSeconds(3);
        var lastPosition = -1;
        var lastChange = DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (focusLost.IsCompleted)
            {
                return new PlaybackOutcome(PlaybackOutcomeKind.Interrupted);
            }

            var position = track.PlaybackHeadPosition;
            if (position >= total)
            {
                return PlaybackOutcome.Completed;
            }

            var now = DateTime.UtcNow;
            if (position != lastPosition)
            {
                lastPosition = position;
                lastChange = now;
            }
            else if (now - lastChange > TimeSpan.FromMilliseconds(300) && position >= total - silentTailFrames)
            {
                return PlaybackOutcome.Completed;
            }
            else if (now - lastChange > StallLimit)
            {
                return new PlaybackOutcome(PlaybackOutcomeKind.Failed, $"Playback stalled at {position}/{total}.");
            }

            if (now > deadline)
            {
                return new PlaybackOutcome(PlaybackOutcomeKind.Failed, "Playback timed out.");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class FocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        private readonly TaskCompletionSource _focusLost;

        public FocusListener(TaskCompletionSource focusLost)
        {
            _focusLost = focusLost;
        }

        public void OnAudioFocusChange(AudioFocus focusChange)
        {
            if (focusChange is AudioFocus.Loss or AudioFocus.LossTransient)
            {
                _focusLost.TrySetResult();
            }
        }
    }
}
