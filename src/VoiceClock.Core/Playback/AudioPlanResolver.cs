using VoiceClock.Core.Time;

namespace VoiceClock.Core.Playback;

/// <summary>The clips that will actually be played for a plan, or what is missing.</summary>
public sealed record ResolvedAnnouncement(IReadOnlyList<AudioSegment> Segments, IReadOnlyList<string> MissingAssetIds, bool UsesFullPhrase)
{
    public bool IsPlayable => MissingAssetIds.Count == 0;
}

/// <summary>
/// Deterministic resolution order:
/// 1. the complete recorded sentence for this exact time, if one exists (plus صباحًا/مساءً when enabled);
/// 2. otherwise the reusable segments from the time engine;
/// 3. otherwise a clear list of the missing recordings. Nothing ever falls back to a network or system voice.
/// </summary>
public static class AudioPlanResolver
{
    public static ResolvedAnnouncement Resolve(TimeSpeechPlan plan, Func<string, bool> isAvailable, bool useFullPhraseOverrides)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(isAvailable);

        if (useFullPhraseOverrides && isAvailable(plan.FullPhraseAssetId))
        {
            var segments = new List<AudioSegment> { new(plan.FullPhraseAssetId, plan.TimeText, SegmentRole.FullPhrase) };
            if (plan.DayPeriodSegment is { } period)
            {
                segments.Add(period);
            }

            return new ResolvedAnnouncement(segments, Missing(segments, isAvailable), UsesFullPhrase: true);
        }

        return new ResolvedAnnouncement(plan.Segments, Missing(plan.Segments, isAvailable), UsesFullPhrase: false);
    }

    private static List<string> Missing(IEnumerable<AudioSegment> segments, Func<string, bool> isAvailable) =>
        segments.Select(s => s.AssetId).Where(id => !isAvailable(id)).Distinct(StringComparer.Ordinal).ToList();
}
