using VoiceClock.Core.Time;

namespace VoiceClock.Core.Catalog;

/// <summary>Groups shown in the guided recording checklist.</summary>
public enum AssetCategory
{
    /// <summary>"الساعة واحدة" … "الساعة اتناشر".</summary>
    Hour,

    /// <summary>Multiples of five: وخمسة، وعشرة، وربع، وتلت، ونص إلا خمسة، ونص، ونص وخمسة، إلا تلت، إلا ربع، إلا عشرة، إلا خمسة.</summary>
    RoundMinute,

    /// <summary>Every other minute: ودقيقة، ودقيقتين، وتلات دقايق، وأربعة وعشرين دقيقة…</summary>
    ExactMinute,

    /// <summary>صباحًا / مساءً.</summary>
    DayPeriod,

    /// <summary>Optional spoken error message.</summary>
    Feedback,

    /// <summary>Optional complete sentence for one specific time.</summary>
    FullPhrase,
}

public enum AssetRequirement
{
    Required,
    RequiredWhenDayPeriodEnabled,
    Optional,
}

/// <summary>One recording the owner can make.</summary>
/// <param name="Id">Stable logical identifier.</param>
/// <param name="Category">Checklist group.</param>
/// <param name="Text">Exactly what to say into the microphone.</param>
/// <param name="Requirement">Whether setup is incomplete without it.</param>
/// <param name="Order">Position in the guided recording sequence.</param>
public sealed record CatalogEntry(string Id, AssetCategory Category, string Text, AssetRequirement Requirement, int Order)
{
    public bool IsRequired(bool includeDayPeriod) => Requirement switch
    {
        AssetRequirement.Required => true,
        AssetRequirement.RequiredWhenDayPeriodEnabled => includeDayPeriod,
        _ => false,
    };
}

/// <summary>
/// Data-driven list of every clip the speech rules can ask for, generated from
/// <see cref="SpeechRules"/> rather than hard-coded in the UI.
/// </summary>
public sealed class RecordingCatalog
{
    private const int FullPhraseOrderBase = 10_000;

    private readonly ITimeSpeechEngine _engine;
    private readonly Dictionary<string, CatalogEntry> _byId;

    public RecordingCatalog(ITimeSpeechEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        var rules = engine.Rules;
        var entries = new List<CatalogEntry>(80);

        for (var h = 1; h <= 12; h++)
        {
            entries.Add(new CatalogEntry(AssetIds.Hour(h), AssetCategory.Hour, rules.HourText(h), AssetRequirement.Required, entries.Count));
        }

        foreach (var rule in rules.Minutes.Where(m => m.IsRound))
        {
            entries.Add(new CatalogEntry(AssetIds.Minute(rule.Minute), AssetCategory.RoundMinute, rule.Text, AssetRequirement.Required, entries.Count));
        }

        foreach (var rule in rules.Minutes.Where(m => !m.IsRound))
        {
            entries.Add(new CatalogEntry(AssetIds.Minute(rule.Minute), AssetCategory.ExactMinute, rule.Text, AssetRequirement.Required, entries.Count));
        }

        entries.Add(new CatalogEntry(AssetIds.DayPeriodAm, AssetCategory.DayPeriod, rules.AmText, AssetRequirement.RequiredWhenDayPeriodEnabled, entries.Count));
        entries.Add(new CatalogEntry(AssetIds.DayPeriodPm, AssetCategory.DayPeriod, rules.PmText, AssetRequirement.RequiredWhenDayPeriodEnabled, entries.Count));
        entries.Add(new CatalogEntry(AssetIds.FeedbackError, AssetCategory.Feedback, rules.FeedbackErrorText, AssetRequirement.Optional, entries.Count));

        Entries = entries.AsReadOnly();
        _byId = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
    }

    /// <summary>All standard clips in guided recording order (full-phrase overrides are not listed).</summary>
    public IReadOnlyList<CatalogEntry> Entries { get; }

    public IEnumerable<CatalogEntry> RequiredEntries(bool includeDayPeriod) => Entries.Where(e => e.IsRequired(includeDayPeriod));

    /// <summary>Finds a standard clip or a full-phrase override; null for anything unknown.</summary>
    public CatalogEntry? Find(string? id)
    {
        if (id is null)
        {
            return null;
        }

        if (_byId.TryGetValue(id, out var entry))
        {
            return entry;
        }

        return AssetIds.TryParseFullPhrase(id, out var hour12, out var minute) ? FullPhrase(hour12, minute) : null;
    }

    public bool IsKnownId(string? id) => Find(id) is not null;

    /// <summary>The optional complete-sentence recording for one 12-hour time.</summary>
    public CatalogEntry FullPhrase(int hour12, int minute)
    {
        var plan = _engine.BuildPlan(new TimeOnly(hour12 % 12, minute), TimeSpeechSettings.Default);
        return new CatalogEntry(
            AssetIds.FullPhrase(hour12, minute),
            AssetCategory.FullPhrase,
            plan.TimeText,
            AssetRequirement.Optional,
            FullPhraseOrderBase + (hour12 * 60) + minute);
    }
}
