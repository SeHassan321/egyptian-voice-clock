namespace VoiceClock.Core.Audio;

public sealed record NormalizationOptions
{
    /// <summary>Average speech level each clip is brought to.</summary>
    public double TargetSpeechRmsDbfs { get; init; } = -20.0;

    /// <summary>Never boost a quiet clip by more than this (keeps background noise down).</summary>
    public double MaxGainDb { get; init; } = 12.0;

    public double MaxCutDb { get; init; } = 12.0;

    /// <summary>No sample may end up louder than this.</summary>
    public double PeakCeilingDbfs { get; init; } = -1.0;
}

public sealed record CompositionOptions
{
    public const int MaxGapMs = 1000;

    /// <summary>Extra silence between clips (each clip already carries short padding).</summary>
    public int GapMs { get; init; } = 60;

    /// <summary>Silence before the first word; some devices clip the start of playback.</summary>
    public int LeadInMs { get; init; } = 120;

    public int TailMs { get; init; } = 150;

    /// <summary>Even out clips recorded at different volumes.</summary>
    public bool NormalizeLoudness { get; init; } = true;

    /// <summary>Output rate; defaults to the first clip's rate.</summary>
    public int? TargetSampleRate { get; init; }

    public NormalizationOptions Normalization { get; init; } = new();
}

/// <summary>Brings a clip to a common speech level without exceeding a peak ceiling.</summary>
public static class LoudnessNormalizer
{
    public static PcmAudio Normalize(PcmAudio clip, NormalizationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        options ??= new NormalizationOptions();

        var analysis = ClipAnalyzer.Analyze(clip);
        if (!analysis.HasSpeech)
        {
            return clip;
        }

        var gainDb = Math.Clamp(options.TargetSpeechRmsDbfs - analysis.SpeechRmsDbfs, -options.MaxCutDb, options.MaxGainDb);
        var peakAfter = analysis.PeakDbfs + gainDb;
        if (peakAfter > options.PeakCeilingDbfs)
        {
            gainDb -= peakAfter - options.PeakCeilingDbfs;
        }

        if (Math.Abs(gainDb) < 0.25)
        {
            return clip;
        }

        var factor = Math.Pow(10.0, gainDb / 20.0);
        var source = clip.Samples;
        var result = new short[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            result[i] = AudioMath.Clamp(source[i] * factor);
        }

        return new PcmAudio(clip.SampleRate, result);
    }
}

/// <summary>
/// Joins recorded clips into one continuous buffer so the announcement plays in a single,
/// gapless stream with deliberate, configurable pauses (no overlapping clips).
/// </summary>
public static class AnnouncementComposer
{
    public static PcmAudio Compose(IReadOnlyList<PcmAudio> clips, CompositionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (clips.Count == 0)
        {
            throw new ArgumentException("At least one clip is required.", nameof(clips));
        }

        options ??= new CompositionOptions();
        var rate = options.TargetSampleRate ?? clips[0].SampleRate;
        var gap = AudioMath.MsToSamples(rate, Math.Clamp(options.GapMs, 0, CompositionOptions.MaxGapMs));
        var lead = AudioMath.MsToSamples(rate, Math.Max(0, options.LeadInMs));
        var tail = AudioMath.MsToSamples(rate, Math.Max(0, options.TailMs));

        var prepared = new List<PcmAudio>(clips.Count);
        foreach (var clip in clips)
        {
            ArgumentNullException.ThrowIfNull(clip);
            var c = clip.SampleRate == rate ? clip : AudioMath.Resample(clip, rate);
            prepared.Add(options.NormalizeLoudness ? LoudnessNormalizer.Normalize(c, options.Normalization) : c);
        }

        var total = lead + tail + (gap * (prepared.Count - 1)) + prepared.Sum(c => c.SampleCount);
        var output = new short[total];
        var position = lead;
        for (var i = 0; i < prepared.Count; i++)
        {
            if (i > 0)
            {
                position += gap;
            }

            Array.Copy(prepared[i].Samples, 0, output, position, prepared[i].SampleCount);
            position += prepared[i].SampleCount;
        }

        return new PcmAudio(rate, output);
    }
}
