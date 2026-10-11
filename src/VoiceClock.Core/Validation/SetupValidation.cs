using VoiceClock.Core.Catalog;
using VoiceClock.Core.Playback;
using VoiceClock.Core.Storage;
using VoiceClock.Core.Time;

namespace VoiceClock.Core.Validation;

/// <summary>A time that cannot be spoken, and why.</summary>
public sealed record UncoveredTime(TimeOnly Time, string Text, IReadOnlyList<string> MissingAssetIds);

public sealed record CoverageReport(int TimesChecked, IReadOnlyList<UncoveredTime> Uncovered, IReadOnlyList<string> MissingAssetIds)
{
    public bool IsComplete => Uncovered.Count == 0;

    public int TimesCovered => TimesChecked - Uncovered.Count;
}

/// <summary>
/// Enumerates every minute of the 12-hour cycle (both halves of the day when صباحًا/مساءً is
/// enabled) and confirms each has a playable audio plan.
/// </summary>
public static class CoverageValidator
{
    /// <param name="useFullPhraseOverrides">
    /// False (the default for completeness) requires the reusable segments alone to cover every time,
    /// so optional complete sentences never hide a missing word.
    /// </param>
    public static CoverageReport Validate(ITimeSpeechEngine engine, Func<string, bool> isAvailable, bool includeDayPeriod, bool useFullPhraseOverrides = false)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(isAvailable);

        var settings = new TimeSpeechSettings(includeDayPeriod);
        var hours = includeDayPeriod ? 24 : 12;
        var uncovered = new List<UncoveredTime>();
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        for (var hour = 0; hour < hours; hour++)
        {
            for (var minute = 0; minute < 60; minute++)
            {
                var plan = engine.BuildPlan(new TimeOnly(hour, minute), settings);
                var resolved = AudioPlanResolver.Resolve(plan, isAvailable, useFullPhraseOverrides);
                if (!resolved.IsPlayable)
                {
                    uncovered.Add(new UncoveredTime(plan.Time, plan.Text, resolved.MissingAssetIds));
                    missing.UnionWith(resolved.MissingAssetIds);
                }
            }
        }

        return new CoverageReport(hours * 60, uncovered, missing.ToList());
    }
}

/// <summary>Everything the setup screen needs to know about completeness.</summary>
public sealed record SetupSummary(
    int RequiredCount,
    int ReadyCount,
    IReadOnlyList<string> MissingIds,
    IReadOnlyList<string> InvalidIds,
    int FullPhraseCount,
    CoverageReport Coverage)
{
    public int MissingCount => MissingIds.Count;

    public int InvalidCount => InvalidIds.Count;

    /// <summary>True only when every required recording is usable and every time can be spoken.</summary>
    public bool IsComplete => MissingCount == 0 && InvalidCount == 0 && Coverage.IsComplete;

    public double Progress => RequiredCount == 0 ? 1 : (double)ReadyCount / RequiredCount;
}

public static class SetupStatusCalculator
{
    public static SetupSummary Compute(RecordingCatalog catalog, IVoiceAssetRepository repository, ITimeSpeechEngine engine, bool includeDayPeriod)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(engine);

        var required = catalog.RequiredEntries(includeDayPeriod).ToList();
        var missing = new List<string>();
        var invalid = new List<string>();
        var ready = 0;

        foreach (var entry in required)
        {
            switch (repository.GetStatus(entry.Id).State)
            {
                case AssetState.Ready:
                    ready++;
                    break;
                case AssetState.Invalid:
                    invalid.Add(entry.Id);
                    break;
                default:
                    missing.Add(entry.Id);
                    break;
            }
        }

        var coverage = CoverageValidator.Validate(engine, repository.IsAvailable, includeDayPeriod);
        return new SetupSummary(required.Count, ready, missing, invalid, repository.AvailableFullPhraseIds.Count, coverage);
    }
}
