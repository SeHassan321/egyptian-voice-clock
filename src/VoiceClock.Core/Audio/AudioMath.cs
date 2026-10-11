namespace VoiceClock.Core.Audio;

/// <summary>Small numeric helpers shared by the audio pipeline.</summary>
public static class AudioMath
{
    public const double FullScale = 32768.0;

    /// <summary>The floor reported for digital silence.</summary>
    public const double SilenceDbfs = -120.0;

    public static double ToDbfs(double amplitude) =>
        amplitude <= 0 ? SilenceDbfs : Math.Max(SilenceDbfs, 20.0 * Math.Log10(amplitude / FullScale));

    public static double FromDbfs(double dbfs) => FullScale * Math.Pow(10.0, dbfs / 20.0);

    public static short Clamp(double value) => (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);

    public static int MsToSamples(int sampleRate, int milliseconds) =>
        milliseconds <= 0 ? 0 : (int)((long)sampleRate * milliseconds / 1000);

    /// <summary>Linear fade-in and fade-out that removes clicks at clip edges.</summary>
    public static void ApplyFades(short[] samples, int fadeInSamples, int fadeOutSamples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var n = samples.Length;
        fadeInSamples = Math.Min(Math.Max(0, fadeInSamples), n);
        fadeOutSamples = Math.Min(Math.Max(0, fadeOutSamples), n);

        for (var i = 0; i < fadeInSamples; i++)
        {
            samples[i] = (short)(samples[i] * (double)i / fadeInSamples);
        }

        for (var i = 0; i < fadeOutSamples; i++)
        {
            var index = n - 1 - i;
            samples[index] = (short)(samples[index] * (double)i / fadeOutSamples);
        }
    }

    /// <summary>Linear-interpolation resampler; adequate for speech between common rates.</summary>
    public static PcmAudio Resample(PcmAudio audio, int targetSampleRate)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.SampleRate == targetSampleRate || audio.IsEmpty)
        {
            return audio.SampleRate == targetSampleRate ? audio : new PcmAudio(targetSampleRate, []);
        }

        var source = audio.Samples;
        var outLength = (int)Math.Round((double)source.Length * targetSampleRate / audio.SampleRate);
        var result = new short[outLength];
        var step = (double)audio.SampleRate / targetSampleRate;

        for (var i = 0; i < outLength; i++)
        {
            var position = i * step;
            var index = (int)position;
            if (index >= source.Length - 1)
            {
                result[i] = source[^1];
                continue;
            }

            var fraction = position - index;
            result[i] = Clamp(source[index] + ((source[index + 1] - source[index]) * fraction));
        }

        return new PcmAudio(targetSampleRate, result);
    }
}
