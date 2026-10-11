using VoiceClock.Core.Audio;

namespace VoiceClock.Core.Tests.TestSupport;

/// <summary>Deterministic synthetic audio used in place of real recordings.</summary>
internal static class Signals
{
    public const int Rate = PcmAudio.StandardSampleRate;

    public static PcmAudio Silence(int ms, int rate = Rate) => new(rate, new short[rate * ms / 1000]);

    /// <summary>A sine tone whose peak is at <paramref name="peakDbfs"/>.</summary>
    public static PcmAudio Tone(int ms, double peakDbfs = -12, double frequency = 440, int rate = Rate)
    {
        var n = rate * ms / 1000;
        var amplitude = AudioMath.FromDbfs(peakDbfs);
        var samples = new short[n];
        for (var i = 0; i < n; i++)
        {
            samples[i] = AudioMath.Clamp(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate));
        }

        return new PcmAudio(rate, samples);
    }

    /// <summary>Low-level pseudo-random hiss (repeatable).</summary>
    public static PcmAudio Noise(int ms, double peakDbfs = -60, int seed = 7, int rate = Rate)
    {
        var random = new Random(seed);
        var amplitude = AudioMath.FromDbfs(peakDbfs);
        var samples = new short[rate * ms / 1000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = AudioMath.Clamp(((random.NextDouble() * 2) - 1) * amplitude);
        }

        return new PcmAudio(rate, samples);
    }

    /// <summary>A soft "s"-like hiss: white noise emphasized toward high frequencies (repeatable).</summary>
    public static PcmAudio Hiss(int ms, double rmsDbfs = -38, int seed = 5, int rate = Rate)
    {
        var random = new Random(seed);
        var raw = new double[rate * ms / 1000];
        double previous = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var white = (random.NextDouble() * 2) - 1;
            raw[i] = white - previous; // first difference: energy concentrated at high frequencies
            previous = white;
        }

        var rms = Math.Sqrt(raw.Average(v => v * v));
        var scale = AudioMath.FromDbfs(rmsDbfs) / rms;
        return new PcmAudio(rate, raw.Select(v => AudioMath.Clamp(v * scale)).ToArray());
    }

    /// <summary>Adds two signals of the same rate, sample by sample (the shorter one is padded with silence).</summary>
    public static PcmAudio Mix(PcmAudio a, PcmAudio b)
    {
        var length = Math.Max(a.SampleCount, b.SampleCount);
        var mixed = new short[length];
        for (var i = 0; i < length; i++)
        {
            var sum = (i < a.SampleCount ? a.Samples[i] : 0) + (i < b.SampleCount ? b.Samples[i] : 0);
            mixed[i] = AudioMath.Clamp(sum);
        }

        return new PcmAudio(a.SampleRate, mixed);
    }

    public static PcmAudio Constant(int ms, short value, int rate = Rate)
    {
        var samples = new short[rate * ms / 1000];
        Array.Fill(samples, value);
        return new PcmAudio(rate, samples);
    }

    public static PcmAudio Concat(params PcmAudio[] parts)
    {
        var rate = parts[0].SampleRate;
        return new PcmAudio(rate, parts.SelectMany(p => p.Samples).ToArray());
    }

    /// <summary>A plausible spoken word: silence, a voiced burst, silence.</summary>
    public static PcmAudio Word(int leadMs = 400, int speechMs = 600, int tailMs = 500, double peakDbfs = -10) =>
        Concat(Noise(leadMs), Tone(speechMs, peakDbfs), Noise(tailMs, seed: 11));
}
