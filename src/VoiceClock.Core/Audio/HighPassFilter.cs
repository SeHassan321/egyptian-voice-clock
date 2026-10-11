namespace VoiceClock.Core.Audio;

/// <summary>
/// Voice "low-cut" filter: a 4th-order Butterworth high-pass at 80 Hz (two cascaded biquads).
/// It removes rumble, handling noise and mains hum (25–60 Hz) that phone microphones pick up,
/// while leaving the voice itself untouched (less than 0.2 dB change above 120 Hz).
/// </summary>
public static class HighPassFilter
{
    public const double DefaultCutoffHz = 80.0;

    // Q values of the two second-order sections of a 4th-order Butterworth filter.
    private const double Q1 = 0.541196100146197;
    private const double Q2 = 1.306562964876377;

    public static PcmAudio Apply(PcmAudio audio, double cutoffHz = DefaultCutoffHz)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.IsEmpty || cutoffHz <= 0)
        {
            return audio;
        }

        if (cutoffHz >= audio.SampleRate / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(cutoffHz), "The cutoff must be below half the sample rate.");
        }

        var input = audio.Samples;

        // Remove any DC offset first so the filter does not start with a step (a click).
        double mean = 0;
        foreach (var sample in input)
        {
            mean += sample;
        }

        mean /= input.Length;

        var first = Biquad.HighPass(audio.SampleRate, cutoffHz, Q1);
        var second = Biquad.HighPass(audio.SampleRate, cutoffHz, Q2);
        var output = new short[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = AudioMath.Clamp(second.Process(first.Process(input[i] - mean)));
        }

        return new PcmAudio(audio.SampleRate, output);
    }

    /// <summary>RBJ cookbook biquad in transposed direct form II.</summary>
    private sealed class Biquad
    {
        private readonly double _b0;
        private readonly double _b1;
        private readonly double _b2;
        private readonly double _a1;
        private readonly double _a2;
        private double _z1;
        private double _z2;

        private Biquad(double b0, double b1, double b2, double a1, double a2)
        {
            _b0 = b0;
            _b1 = b1;
            _b2 = b2;
            _a1 = a1;
            _a2 = a2;
        }

        public static Biquad HighPass(int sampleRate, double cutoffHz, double q)
        {
            var w0 = 2 * Math.PI * cutoffHz / sampleRate;
            var cos = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2 * q);
            var a0 = 1 + alpha;
            return new Biquad(
                (1 + cos) / 2 / a0,
                -(1 + cos) / a0,
                (1 + cos) / 2 / a0,
                -2 * cos / a0,
                (1 - alpha) / a0);
        }

        public double Process(double x)
        {
            var y = (_b0 * x) + _z1;
            _z1 = (_b1 * x) - (_a1 * y) + _z2;
            _z2 = (_b2 * x) - (_a2 * y);
            return y;
        }
    }
}
