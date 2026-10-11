namespace VoiceClock.Core.Settings;

/// <summary>
/// User preferences. Playback speed is deliberately absent: changing it without pitch artifacts
/// would distort the recorded voice. Automatic announcements are not part of this version.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public const int MinSegmentGapMs = 0;
    public const int MaxSegmentGapMs = 400;
    public const int DefaultSegmentGapMs = 60;

    public static AppSettings Default { get; } = new();

    /// <summary>Say "صباحًا"/"مساءً" after the time. Off by default.</summary>
    public bool IncludeDayPeriod { get; init; }

    /// <summary>Extra pause between recorded pieces, in milliseconds.</summary>
    public int SegmentGapMs { get; init; } = DefaultSegmentGapMs;

    /// <summary>Even out pieces recorded at different volumes.</summary>
    public bool NormalizeLoudness { get; init; } = true;

    /// <summary>Prefer a complete recorded sentence for a time when one exists.</summary>
    public bool UseFullPhraseOverrides { get; init; } = true;

    /// <summary>Beep/vibrate (or play the recorded error clip) when the time cannot be spoken.</summary>
    public bool PlayErrorFeedback { get; init; } = true;

    public AppSettings Sanitized() => this with
    {
        SegmentGapMs = Math.Clamp(SegmentGapMs, MinSegmentGapMs, MaxSegmentGapMs),
    };
}
