using Android.Content;
using Android.Media;
using VoiceClock.Core.Audio;
using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.Playback;
using AEncoding = Android.Media.Encoding;

namespace VoiceClock.App.Droid;

/// <summary>
/// Plays a composed announcement as one continuous PCM stream through <see cref="AudioTrack"/>:
/// no gaps or clicks between words, transient audio focus (other apps duck), and nothing played
/// during a call. Completion and failures are decided by <see cref="StreamPlayer"/>, which is
/// unit-tested against simulated phones (see StreamPlayerTests).
/// </summary>
public sealed class AndroidAudioOutput : IAudioOutput
{
    private readonly IAppLog _log;
    private readonly IPlaybackClock _clock = new SystemPlaybackClock();

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
            var minBuffer = AudioTrack.GetMinBufferSize(audio.SampleRate, ChannelOut.Mono, AEncoding.Pcm16bit);
            if (minBuffer <= 0)
            {
                return Fail($"Unsupported output format ({minBuffer}).");
            }

            var format = new AudioFormat.Builder()
                .SetSampleRate(audio.SampleRate)!
                .SetEncoding(AEncoding.Pcm16bit)!
                .SetChannelMask(ChannelOut.Mono)!
                .Build()!;

            track = new AudioTrack.Builder()
                .SetAudioAttributes(attributes)!
                .SetAudioFormat(format)!
                .SetBufferSizeInBytes(Math.Max(minBuffer * 2, audio.SampleRate / 5 * 2))!
                .SetTransferMode(AudioTrackMode.Stream)!
                .Build()!;

            if (track.State != AudioTrackState.Initialized)
            {
                return Fail("Audio output could not be initialized.");
            }

            var outcome = await StreamPlayer.PlayAsync(
                new AudioTrackSink(track),
                audio,
                () => focusLost.Task.IsCompleted,
                _clock,
                cancellationToken: cancellationToken);

            if (outcome.Kind == PlaybackOutcomeKind.Failed)
            {
                _log.Warn($"Playback failed: {outcome.Detail}");
            }

            return outcome;
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
                track.Release();
                track.Dispose();
            }

            audioManager.AbandonAudioFocusRequest(focusRequest);
        }
    }

    private PlaybackOutcome Fail(string detail)
    {
        _log.Warn("Playback failed: " + detail);
        return new PlaybackOutcome(PlaybackOutcomeKind.Failed, detail);
    }

    /// <summary>Adapts an <see cref="AudioTrack"/> in streaming mode to <see cref="IPcmStreamSink"/>.</summary>
    private sealed class AudioTrackSink : IPcmStreamSink
    {
        private readonly AudioTrack _track;

        public AudioTrackSink(AudioTrack track)
        {
            _track = track;
            BufferFrames = track.BufferSizeInFrames;
        }

        public int BufferFrames { get; }

        // The Java value is an unsigned 32-bit frame counter.
        public long PlaybackPosition => (uint)_track.PlaybackHeadPosition;

        public void Play() => _track.Play();

        public int Write(short[] samples, int offset, int count) =>
            _track.Write(samples, offset, count, WriteMode.NonBlocking);

        public void Stop() => _track.Stop();

        public void Abort()
        {
            try
            {
                _track.Pause();
                _track.Flush();
            }
            catch (Java.Lang.IllegalStateException)
            {
            }
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
