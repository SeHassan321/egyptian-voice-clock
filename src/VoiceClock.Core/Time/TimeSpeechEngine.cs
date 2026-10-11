namespace VoiceClock.Core.Time;

/// <summary>Turns a clock time into an ordered list of recorded clips.</summary>
public interface ITimeSpeechEngine
{
    SpeechRules Rules { get; }

    TimeSpeechPlan BuildPlan(TimeOnly time, TimeSpeechSettings settings);
}

/// <summary>
/// Pure, deterministic interpreter for the spoken Egyptian 12-hour clock. It has no
/// dependency on the UI, audio, Android or the file system.
/// </summary>
/// <remarks>
/// Rules (all wording comes from <see cref="SpeechRules"/>):
/// <list type="bullet">
/// <item>Minute 00 → "الساعة" + hour.</item>
/// <item>A minute whose rule is <see cref="MinuteRelation.ToNextHour"/> (by default 40, 45, 50, 55)
/// → next hour + "إلا …", wrapping 12 → 1.</item>
/// <item>Every other minute → current hour + "و…".</item>
/// <item>"صباحًا"/"مساءً" is appended when enabled, based on the real time (00:00–11:59 is morning).</item>
/// </list>
/// </remarks>
public sealed class TimeSpeechEngine : ITimeSpeechEngine
{
    public TimeSpeechEngine(SpeechRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Rules = rules;
    }

    public SpeechRules Rules { get; }

    public TimeSpeechPlan BuildPlan(TimeOnly time, TimeSpeechSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var minute = time.Minute;
        var wholeMinute = new TimeOnly(time.Hour, minute);
        var hour12 = ToHour12(time.Hour);
        var period = time.Hour < 12 ? DayPeriod.Am : DayPeriod.Pm;

        MinuteRelation relation;
        int spokenHour;
        MinuteRule? minuteRule = null;

        if (minute == 0)
        {
            relation = MinuteRelation.OnTheHour;
            spokenHour = hour12;
        }
        else
        {
            minuteRule = Rules.Minute(minute);
            relation = minuteRule.Relation;
            spokenHour = relation == MinuteRelation.ToNextHour ? NextHour(hour12) : hour12;
        }

        var timeSegments = new List<AudioSegment>(2)
        {
            new(AssetIds.Hour(spokenHour), Rules.HourText(spokenHour), SegmentRole.Hour),
        };

        if (minuteRule is not null)
        {
            timeSegments.Add(new AudioSegment(AssetIds.Minute(minute), minuteRule.Text, SegmentRole.Minute));
        }

        var periodSegment = settings.IncludeDayPeriod
            ? new AudioSegment(AssetIds.Period(period), Rules.DayPeriodText(period), SegmentRole.DayPeriod)
            : null;

        return new TimeSpeechPlan(wholeMinute, hour12, spokenHour, relation, period, timeSegments.AsReadOnly(), periodSegment);
    }

    /// <summary>0 and 12 → 12, 13 → 1, … 23 → 11.</summary>
    public static int ToHour12(int hour24)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hour24);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hour24, 23);
        var h = hour24 % 12;
        return h == 0 ? 12 : h;
    }

    /// <summary>The hour after <paramref name="hour12"/> on a 12-hour clock (12 → 1).</summary>
    public static int NextHour(int hour12)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hour12, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hour12, 12);
        return hour12 == 12 ? 1 : hour12 + 1;
    }
}
