using System.Globalization;
using System.Text.Json;
using VoiceClock.Core.Serialization;

namespace VoiceClock.Core.Time;

/// <summary>How one minute value (1–59) is spoken.</summary>
/// <param name="Minute">The minute value.</param>
/// <param name="Relation">Whether it follows the current hour or precedes the next one.</param>
/// <param name="Text">The exact words of the clip, including "و" or "إلا".</param>
public sealed record MinuteRule(int Minute, MinuteRelation Relation, string Text)
{
    /// <summary>Multiples of five use the familiar fractions (وخمسة، وربع، وتلت، ونص…).</summary>
    public bool IsRound => Minute % 5 == 0;
}

/// <summary>
/// The validated Egyptian Arabic phrase dictionary. It is data, not code: every hour word,
/// every minute expression and its relation to the hour come from
/// <c>Rules/speech-rules-ar-EG.json</c>.
/// </summary>
public sealed class SpeechRules
{
    public const int SupportedSchemaVersion = 1;
    public const string DefaultResourceName = "VoiceClock.Core.Rules.speech-rules-ar-EG.json";

    private static readonly Lazy<SpeechRules> DefaultRules = new(LoadEmbeddedDefault);

    private readonly string[] _hours;          // index 1..12
    private readonly MinuteRule[] _minutes;    // index 1..59

    private SpeechRules(string locale, string[] hours, MinuteRule[] minutes, string am, string pm, string feedbackError)
    {
        Locale = locale;
        _hours = hours;
        _minutes = minutes;
        AmText = am;
        PmText = pm;
        FeedbackErrorText = feedbackError;
        Minutes = minutes.Skip(1).ToArray();
    }

    public string Locale { get; }

    public string AmText { get; }

    public string PmText { get; }

    public string FeedbackErrorText { get; }

    /// <summary>Minute rules for 1–59 in order.</summary>
    public IReadOnlyList<MinuteRule> Minutes { get; }

    /// <summary>The Egyptian Arabic rules shipped with the app.</summary>
    public static SpeechRules Default => DefaultRules.Value;

    public string HourText(int hour12)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hour12, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hour12, 12);
        return _hours[hour12];
    }

    public MinuteRule Minute(int minute)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minute, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minute, 59);
        return _minutes[minute];
    }

    public string DayPeriodText(DayPeriod period) => period == DayPeriod.Am ? AmText : PmText;

    /// <summary>Parses and validates a rules document. Throws <see cref="SpeechRulesException"/> listing every problem.</summary>
    public static SpeechRules Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        SpeechRulesDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, CoreJsonContext.Default.SpeechRulesDocument);
        }
        catch (JsonException ex)
        {
            throw new SpeechRulesException(["The rules file is not valid JSON: " + ex.Message]);
        }

        if (document is null)
        {
            throw new SpeechRulesException(["The rules file is empty."]);
        }

        var problems = new List<string>();

        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            problems.Add($"Unsupported schemaVersion {document.SchemaVersion}; expected {SupportedSchemaVersion}.");
        }

        var hours = new string[13];
        if (document.Hours is null)
        {
            problems.Add("Missing 'hours'.");
        }
        else
        {
            foreach (var key in document.Hours.Keys)
            {
                if (!TryParseKey(key, 1, 12, out _))
                {
                    problems.Add($"Unexpected hour key '{key}'. Hours must be 1-12.");
                }
            }

            for (var h = 1; h <= 12; h++)
            {
                if (document.Hours.TryGetValue(h.ToString(CultureInfo.InvariantCulture), out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    hours[h] = text.Trim();
                }
                else
                {
                    problems.Add($"Missing text for hour {h}.");
                }
            }
        }

        var minutes = new MinuteRule[60];
        if (document.Minutes is null)
        {
            problems.Add("Missing 'minutes'.");
        }
        else
        {
            foreach (var key in document.Minutes.Keys)
            {
                if (!TryParseKey(key, 1, 59, out _))
                {
                    problems.Add($"Unexpected minute key '{key}'. Minutes must be 1-59 (minute 0 is always just the hour).");
                }
            }

            for (var m = 1; m <= 59; m++)
            {
                if (!document.Minutes.TryGetValue(m.ToString(CultureInfo.InvariantCulture), out var rule) || rule is null)
                {
                    problems.Add($"Missing rule for minute {m}.");
                    continue;
                }

                MinuteRelation? relation = rule.Relation switch
                {
                    "past" => MinuteRelation.PastCurrentHour,
                    "to" => MinuteRelation.ToNextHour,
                    _ => null,
                };

                if (relation is null)
                {
                    problems.Add($"Minute {m} has relation '{rule.Relation}'; use 'past' or 'to'.");
                }

                if (string.IsNullOrWhiteSpace(rule.Text))
                {
                    problems.Add($"Missing text for minute {m}.");
                }

                if (relation is not null && !string.IsNullOrWhiteSpace(rule.Text))
                {
                    minutes[m] = new MinuteRule(m, relation.Value, rule.Text.Trim());
                }
            }
        }

        var am = document.DayPeriods?.Am?.Trim();
        var pm = document.DayPeriods?.Pm?.Trim();
        if (string.IsNullOrEmpty(am) || string.IsNullOrEmpty(pm))
        {
            problems.Add("Missing 'dayPeriods.am' or 'dayPeriods.pm'.");
        }

        var feedbackError = document.Feedback?.Error?.Trim();
        if (string.IsNullOrEmpty(feedbackError))
        {
            problems.Add("Missing 'feedback.error'.");
        }

        if (problems.Count > 0)
        {
            throw new SpeechRulesException(problems);
        }

        return new SpeechRules(
            string.IsNullOrWhiteSpace(document.Locale) ? "ar-EG" : document.Locale.Trim(),
            hours,
            minutes,
            am!,
            pm!,
            feedbackError!);
    }

    private static SpeechRules LoadEmbeddedDefault()
    {
        using var stream = typeof(SpeechRules).Assembly.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException($"Embedded rules '{DefaultResourceName}' were not found.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private static bool TryParseKey(string key, int min, int max, out int value) =>
        int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
        value >= min && value <= max &&
        key == value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Raised when a rules document is incomplete or malformed.</summary>
public sealed class SpeechRulesException : Exception
{
    public SpeechRulesException(IReadOnlyList<string> problems)
        : base("Invalid speech rules: " + string.Join(" ", problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }
}

internal sealed class SpeechRulesDocument
{
    public int SchemaVersion { get; set; }

    public string? Locale { get; set; }

    public Dictionary<string, string>? Hours { get; set; }

    public Dictionary<string, MinuteRuleDocument?>? Minutes { get; set; }

    public DayPeriodsDocument? DayPeriods { get; set; }

    public FeedbackDocument? Feedback { get; set; }
}

internal sealed class MinuteRuleDocument
{
    public string? Relation { get; set; }

    public string? Text { get; set; }
}

internal sealed class DayPeriodsDocument
{
    public string? Am { get; set; }

    public string? Pm { get; set; }
}

internal sealed class FeedbackDocument
{
    public string? Error { get; set; }
}
