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

            // Do not call Stop() yet: on Android it makes PlaybackHeadPosition read 0 immediately,
            // which would hide whether the audio was really played.
            var outcome = await WaitUntilPlayedAsync(track, total, audio, focusLost.Task, cancellationToken);
            if (outcome.Succeeded)
            {
                track.Stop();
            }

            return outcome;
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

    /// <summary>
    /// Waits until the mixer has consumed everything except a short tail (always silence in composed
    /// announcements), then until the speaker has actually presented it (AudioTimestamp), so Bluetooth
    /// and other high-latency outputs never lose the last syllable.
    /// </summary>
    private static async Task<PlaybackOutcome> WaitUntilPlayedAsync(AudioTrack track, int total, PcmAudio audio, Task focusLost, CancellationToken cancellationToken)
    {
        var target = total - Math.Min(total / 10, audio.SampleRate * 60 / 1000);
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
            if (position >= target)
            {
                break;
            }

            var now = DateTime.UtcNow;
            if (position != lastPosition)
            {
                lastPosition = position;
                lastChange = now;
            }
            else if (now - lastChange > StallLimit)
            {
                return new PlaybackOutcome(PlaybackOutcomeKind.Failed, $"Playback stalled at {position}/{total}.");
            }

            if (now > deadline)
            {
                return new PlaybackOutcome(PlaybackOutcomeKind.Failed, $"Playback timed out at {position}/{total}.");
            }

            await Task.Delay(15, cancellationToken);
        }

        await WaitForPresentationAsync(track, target, cancellationToken);
        return focusLost.IsCompleted ? new PlaybackOutcome(PlaybackOutcomeKind.Interrupted) : PlaybackOutcome.Completed;
    }

    /// <summary>Waits (at most 600 ms) until the output reports the target frame as heard.</summary>
    private static async Task WaitForPresentationAsync(AudioTrack track, long targetFrame, CancellationToken cancellationToken)
    {
        var timestamp = new AudioTimestamp();
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
        while (DateTime.UtcNow < until)
        {
            if (track.GetTimestamp(timestamp) && timestamp.FramePosition >= targetFrame)
            {
                return;
            }

            await Task.Delay(15, cancellationToken);
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
