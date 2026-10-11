using VoiceClock.Core.Audio;

namespace VoiceClock.Core.Playback;

public enum PlaybackOutcomeKind
{
    /// <summary>The platform player confirmed the whole buffer was played.</summary>
    Completed,

    /// <summary>Stopped on request (new tap, leaving the screen, app going to background).</summary>
    Cancelled,

    /// <summary>Another app or the system took the audio away mid-way.</summary>
    Interrupted,

    /// <summary>Not played because a phone call is active.</summary>
    BlockedByCall,

    /// <summary>The system refused audio focus.</summary>
    FocusDenied,

    /// <summary>Something else is already using the audio.</summary>
    Busy,

    /// <summary>The player reported an error.</summary>
    Failed,
}

public sealed record PlaybackOutcome(PlaybackOutcomeKind Kind, string? Detail = null)
{
    public static PlaybackOutcome Completed { get; } = new(PlaybackOutcomeKind.Completed);

    public static PlaybackOutcome Cancelled { get; } = new(PlaybackOutcomeKind.Cancelled);

    public static PlaybackOutcome Busy { get; } = new(PlaybackOutcomeKind.Busy);

    public bool Succeeded => Kind == PlaybackOutcomeKind.Completed;
}

/// <summary>Platform audio player.</summary>
public interface IAudioOutput
{
    /// <summary>
    /// Plays the buffer to the end. Implementations must return <see cref="PlaybackOutcome.Completed"/>
    /// only when the underlying player confirms the audio was played, never optimistically.
    /// </summary>
    Task<PlaybackOutcome> PlayAsync(PcmAudio audio, CancellationToken cancellationToken);
}

/// <summary>Non-visual feedback for people who cannot read the screen.</summary>
public interface IFeedbackService
{
    /// <summary>A short haptic tick confirming a press.</summary>
    void Tap();

    /// <summary>A distinct beep and vibration meaning "the time could not be spoken".</summary>
    Task SignalErrorAsync(CancellationToken cancellationToken);
}

public sealed class NullFeedbackService : IFeedbackService
{
    public static NullFeedbackService Instance { get; } = new();

    public void Tap()
    {
    }

    public Task SignalErrorAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The device's local wall-clock time, exactly as the phone shows it.</summary>
public interface ILocalClock
{
    TimeOnly Now();
}

public sealed class SystemLocalClock : ILocalClock
{
    public TimeOnly Now()
    {
        // The phone's time zone may have changed since the last call.
        TimeZoneInfo.ClearCachedData();
        return TimeOnly.FromDateTime(DateTime.Now);
    }
}
