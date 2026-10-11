namespace VoiceClock.Core.Audio;

/// <summary>Problems found in a recording. The first four make it unusable; the rest are warnings.</summary>
public enum ClipIssue
{
    Empty,
    NoSpeech,
    TooShort,
    TooLong,
    TooQuiet,
    Clipped,
}

public sealed record ClipProcessingOptions
{
    public static ClipProcessingOptions Default { get; } = new();

    /// <summary>Remove leading and trailing silence (keeping the padding below).</summary>
    public bool TrimSilence { get; init; } = true;

    public int LeadPaddingMs { get; init; } = 40;

    /// <summary>Kept generous so soft endings such as "دقيقة" are never cut.</summary>
    public int TailPaddingMs { get; init; } = 110;

    public int FadeInMs { get; init; } = 5;

    public int FadeOutMs { get; init; } = 15;

    public int MinSpeechMs { get; init; } = 150;

    public int MaxDurationMs { get; init; } = 12_000;

    public double QuietPeakDbfs { get; init; } = -26.0;

    public double MaxClippedFraction { get; init; } = 0.002;
}

/// <summary>A recording after validation and gentle cleanup.</summary>
public sealed record ProcessedClip(PcmAudio Audio, ClipAnalysis Analysis, IReadOnlyList<ClipIssue> Issues)
{
    public bool IsValid => !Issues.Any(IsBlocking);

    public IEnumerable<ClipIssue> Warnings => Issues.Where(i => !IsBlocking(i));

    public static bool IsBlocking(ClipIssue issue) =>
        issue is ClipIssue.Empty or ClipIssue.NoSpeech or ClipIssue.TooShort or ClipIssue.TooLong;
}

/// <summary>
/// Validates a raw recording and trims silence safely: the detected speech is kept intact with
/// padding on both sides, and short fades prevent clicks. Nothing else is altered.
/// </summary>
public static class ClipProcessor
{
    public static ProcessedClip Process(PcmAudio raw, ClipProcessingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        options ??= ClipProcessingOptions.Default;

        var analysis = ClipAnalyzer.Analyze(raw);
        if (raw.IsEmpty)
        {
            return new ProcessedClip(raw, analysis, [ClipIssue.Empty]);
        }

        if (!analysis.HasSpeech)
        {
            return new ProcessedClip(raw, analysis, [ClipIssue.NoSpeech]);
        }

        var issues = new List<ClipIssue>();
        var rate = raw.SampleRate;

        if (analysis.SpeechSampleCount < AudioMath.MsToSamples(rate, options.MinSpeechMs))
        {
            issues.Add(ClipIssue.TooShort);
        }

        var start = 0;
        var end = raw.SampleCount;
        if (options.TrimSilence)
        {
            start = Math.Max(0, analysis.SpeechStartSample - AudioMath.MsToSamples(rate, options.LeadPaddingMs));
            end = Math.Min(raw.SampleCount, analysis.SpeechEndSample + AudioMath.MsToSamples(rate, options.TailPaddingMs));
        }

        var trimmed = raw.Slice(start, end - start);
        AudioMath.ApplyFades(trimmed.Samples, AudioMath.MsToSamples(rate, options.FadeInMs), AudioMath.MsToSamples(rate, options.FadeOutMs));

        if (trimmed.SampleCount > AudioMath.MsToSamples(rate, options.MaxDurationMs))
        {
            issues.Add(ClipIssue.TooLong);
        }

        if (analysis.PeakDbfs < options.QuietPeakDbfs)
        {
            issues.Add(ClipIssue.TooQuiet);
        }

        if (analysis.ClippedFraction > options.MaxClippedFraction)
        {
            issues.Add(ClipIssue.Clipped);
        }

        return new ProcessedClip(trimmed, ClipAnalyzer.Analyze(trimmed), issues);
    }
}
