namespace VoiceClock.Core.Audio;

/// <summary>
/// Mono, 16-bit linear PCM audio. Recordings, bundled clips and composed announcements all use
/// this one representation, which keeps validation, silence trimming and gapless concatenation
/// exact and testable.
/// </summary>
public sealed class PcmAudio
{
    /// <summary>The rate every Android device is guaranteed to record at.</summary>
    public const int StandardSampleRate = 44100;
    public const int MinSampleRate = 8000;
    public const int MaxSampleRate = 96000;

    public PcmAudio(int sampleRate, short[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, MinSampleRate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, MaxSampleRate);
        SampleRate = sampleRate;
        Samples = samples;
    }

    public int SampleRate { get; }

    /// <summary>The samples. Treat as read-only; processing always produces new instances.</summary>
    public short[] Samples { get; }

    public int SampleCount => Samples.Length;

    public bool IsEmpty => Samples.Length == 0;

    public TimeSpan Duration => TimeSpan.FromTicks((long)Samples.Length * TimeSpan.TicksPerSecond / SampleRate);

    public static PcmAudio Silence(int sampleRate, TimeSpan duration) =>
        new(sampleRate, new short[SamplesFor(sampleRate, duration)]);

    public static int SamplesFor(int sampleRate, TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 0 : (int)Math.Round(duration.TotalSeconds * sampleRate);

    public PcmAudio Slice(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, Samples.Length);
        return new PcmAudio(SampleRate, Samples.AsSpan(start, length).ToArray());
    }
}
