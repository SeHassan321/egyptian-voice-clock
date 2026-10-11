using VoiceClock.Core.Audio;
using VoiceClock.Core.Playback;
using VoiceClock.Core.Recording;

namespace VoiceClock.Core.Tests.TestSupport;

internal sealed class FakeAudioOutput : IAudioOutput
{
    private readonly object _sync = new();
    private readonly List<PcmAudio> _played = [];

    /// <summary>When set, playback waits until this completes (or is cancelled).</summary>
    public TaskCompletionSource? Blocker { get; set; }

    public PlaybackOutcome NextOutcome { get; set; } = PlaybackOutcome.Completed;

    /// <summary>Signalled as soon as playback starts.</summary>
    public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<PcmAudio> Played
    {
        get
        {
            lock (_sync)
            {
                return _played.ToList();
            }
        }
    }

    public async Task<PlaybackOutcome> PlayAsync(PcmAudio audio, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _played.Add(audio);
        }

        Started.TrySetResult();
        if (Blocker is { } blocker)
        {
            await blocker.Task.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return NextOutcome;
    }

    public void ResetStarted() => Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class FakeFeedback : IFeedbackService
{
    public int Taps { get; private set; }

    public int Errors { get; private set; }

    public void Tap() => Taps++;

    public Task SignalErrorAsync(CancellationToken cancellationToken)
    {
        Errors++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeClock : ILocalClock
{
    public TimeOnly Time { get; set; } = new(1, 40);

    public TimeOnly Now() => Time;
}

internal sealed class FakeAudioInput : IAudioInput
{
    public PcmAudio NextRecording { get; set; } = Signals.Word();

    public bool Unavailable { get; set; }

    public FakeCapture? LastCapture { get; private set; }

    public int Starts { get; private set; }

    public Task<IAudioCapture> StartAsync(int sampleRate, TimeSpan maxDuration, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new AudioInputException("Microphone busy.");
        }

        Starts++;
        LastCapture = new FakeCapture(NextRecording);
        return Task.FromResult<IAudioCapture>(LastCapture);
    }
}

internal sealed class FakeCapture : IAudioCapture
{
    private readonly PcmAudio _recording;

    public FakeCapture(PcmAudio recording)
    {
        _recording = recording;
    }

    public event EventHandler<float>? LevelChanged;

    public event EventHandler? LimitReached;

    public TimeSpan Elapsed => _recording.Duration;

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    public Task<PcmAudio> StopAsync()
    {
        Stopped = true;
        return Task.FromResult(_recording);
    }

    public void RaiseLevel(float level) => LevelChanged?.Invoke(this, level);

    public void RaiseLimit() => LimitReached?.Invoke(this, EventArgs.Empty);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
