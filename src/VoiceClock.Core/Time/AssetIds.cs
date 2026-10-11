using System.Globalization;

namespace VoiceClock.Core.Time;

/// <summary>
/// Stable logical identifiers for every recording. File names are derived from these
/// identifiers, but nothing else in the app depends on a physical path.
/// </summary>
public static class AssetIds
{
    public const string HourPrefix = "hour.";
    public const string MinutePrefix = "minute.";
    public const string FullPhrasePrefix = "phrase.";
    public const string DayPeriodAm = "period.am";
    public const string DayPeriodPm = "period.pm";
    public const string FeedbackError = "feedback.error";

    /// <summary>"hour.01" … "hour.12": "الساعة واحدة" … "الساعة اتناشر".</summary>
    public static string Hour(int hour12)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hour12, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hour12, 12);
        return HourPrefix + TwoDigits(hour12);
    }

    /// <summary>"minute.01" … "minute.59": the minute expression including its connector.</summary>
    public static string Minute(int minute)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minute, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minute, 59);
        return MinutePrefix + TwoDigits(minute);
    }

    public static string Period(DayPeriod period) => period == DayPeriod.Am ? DayPeriodAm : DayPeriodPm;

    /// <summary>
    /// "phrase.HH.MM": an optional complete recorded sentence for one 12-hour time
    /// (without صباحًا/مساءً) that replaces the composed segments.
    /// </summary>
    public static string FullPhrase(int hour12, int minute)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hour12, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hour12, 12);
        ArgumentOutOfRangeException.ThrowIfNegative(minute);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minute, 59);
        return FullPhrasePrefix + TwoDigits(hour12) + "." + TwoDigits(minute);
    }

    public static bool TryParseFullPhrase(string? id, out int hour12, out int minute)
    {
        hour12 = 0;
        minute = 0;
        if (id is null || id.Length != FullPhrasePrefix.Length + 5 || !id.StartsWith(FullPhrasePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = id.AsSpan(FullPhrasePrefix.Length);
        if (rest[2] != '.' ||
            !TryParseTwoDigits(rest[..2], out var h) ||
            !TryParseTwoDigits(rest[3..], out var m) ||
            h is < 1 or > 12 ||
            m is < 0 or > 59)
        {
            return false;
        }

        hour12 = h;
        minute = m;
        return true;
    }

    public static bool IsFullPhrase(string? id) => TryParseFullPhrase(id, out _, out _);

    /// <summary>True for every identifier the app can ever store or import.</summary>
    public static bool IsWellFormed(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        if (id == DayPeriodAm || id == DayPeriodPm || id == FeedbackError)
        {
            return true;
        }

        if (id.StartsWith(HourPrefix, StringComparison.Ordinal) && id.Length == HourPrefix.Length + 2)
        {
            return TryParseTwoDigits(id.AsSpan(HourPrefix.Length), out var h) && h is >= 1 and <= 12;
        }

        if (id.StartsWith(MinutePrefix, StringComparison.Ordinal) && id.Length == MinutePrefix.Length + 2)
        {
            return TryParseTwoDigits(id.AsSpan(MinutePrefix.Length), out var m) && m is >= 1 and <= 59;
        }

        return IsFullPhrase(id);
    }

    private static string TwoDigits(int value) => value.ToString("00", CultureInfo.InvariantCulture);

    private static bool TryParseTwoDigits(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        if (text.Length != 2 || !char.IsAsciiDigit(text[0]) || !char.IsAsciiDigit(text[1]))
        {
            return false;
        }

        value = ((text[0] - '0') * 10) + (text[1] - '0');
        return true;
    }
}
