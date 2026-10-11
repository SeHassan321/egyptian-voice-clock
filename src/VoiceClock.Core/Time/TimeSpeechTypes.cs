namespace VoiceClock.Core.Time;

/// <summary>Morning or evening half of the day, taken from the real (24-hour) time.</summary>
public enum DayPeriod
{
    /// <summary>00:00–11:59, spoken as "صباحًا".</summary>
    Am,

    /// <summary>12:00–23:59, spoken as "مساءً".</summary>
    Pm,
}

/// <summary>How the minutes are expressed relative to an hour.</summary>
public enum MinuteRelation
{
    /// <summary>Minute 00: only the hour is spoken ("الساعة واحدة").</summary>
    OnTheHour,

    /// <summary>Spoken after the current hour ("الساعة واحدة وربع").</summary>
    PastCurrentHour,

    /// <summary>Spoken before the next hour with "إلا" ("الساعة اتنين إلا ربع").</summary>
    ToNextHour,
}

/// <summary>What a segment of a spoken announcement represents.</summary>
public enum SegmentRole
{
    Hour,
    Minute,
    DayPeriod,
    FullPhrase,
}

/// <summary>One recorded clip in an announcement, in playback order.</summary>
/// <param name="AssetId">Stable logical identifier of the recording (never a file path).</param>
/// <param name="Text">The Egyptian Arabic words this clip contains.</param>
/// <param name="Role">What part of the time the clip expresses.</param>
public sealed record AudioSegment(string AssetId, string Text, SegmentRole Role);

/// <summary>Options that change what the engine says.</summary>
/// <param name="IncludeDayPeriod">Append "صباحًا" or "مساءً". Off by default.</param>
public sealed record TimeSpeechSettings(bool IncludeDayPeriod = false)
{
    public static TimeSpeechSettings Default { get; } = new();
}
