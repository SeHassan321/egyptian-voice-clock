namespace VoiceClock.Core.Time;

/// <summary>
/// The deterministic result of interpreting one time: which clips to play, in order,
/// and the sentence they form.
/// </summary>
public sealed class TimeSpeechPlan
{
    internal TimeSpeechPlan(
        TimeOnly time,
        int hour12,
        int spokenHour,
        MinuteRelation relation,
        DayPeriod dayPeriod,
        IReadOnlyList<AudioSegment> timeSegments,
        AudioSegment? dayPeriodSegment)
    {
        Time = time;
        Hour12 = hour12;
        SpokenHour = spokenHour;
        Relation = relation;
        DayPeriod = dayPeriod;
        TimeSegments = timeSegments;
        DayPeriodSegment = dayPeriodSegment;
        Segments = dayPeriodSegment is null ? timeSegments : [.. timeSegments, dayPeriodSegment];
        FullPhraseAssetId = AssetIds.FullPhrase(hour12, time.Minute);
    }

    /// <summary>The interpreted time, truncated to the minute.</summary>
    public TimeOnly Time { get; }

    /// <summary>The hour as a 12-hour clock shows it (1–12).</summary>
    public int Hour12 { get; }

    public int Minute => Time.Minute;

    /// <summary>The hour word actually spoken: the next hour for "إلا" expressions.</summary>
    public int SpokenHour { get; }

    public MinuteRelation Relation { get; }

    public DayPeriod DayPeriod { get; }

    public bool IncludesDayPeriod => DayPeriodSegment is not null;

    /// <summary>The hour and minute clips (no day period).</summary>
    public IReadOnlyList<AudioSegment> TimeSegments { get; }

    /// <summary>"صباحًا"/"مساءً" when enabled in settings.</summary>
    public AudioSegment? DayPeriodSegment { get; }

    /// <summary>Every clip in playback order.</summary>
    public IReadOnlyList<AudioSegment> Segments { get; }

    /// <summary>The optional complete-sentence recording that may replace <see cref="TimeSegments"/>.</summary>
    public string FullPhraseAssetId { get; }

    /// <summary>The full sentence, e.g. "الساعة اتنين إلا ربع".</summary>
    public string Text => string.Join(' ', Segments.Select(s => s.Text));

    /// <summary>The sentence without the day period, e.g. for labelling a full-phrase recording.</summary>
    public string TimeText => string.Join(' ', TimeSegments.Select(s => s.Text));

    public override string ToString() => $"{Time:HH:mm} → {Text}";
}
