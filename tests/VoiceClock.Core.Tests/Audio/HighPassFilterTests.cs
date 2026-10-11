using VoiceClock.Core.Audio;
using VoiceClock.Core.Tests.TestSupport;

namespace VoiceClock.Core.Tests.Audio;

public class HighPassFilterTests
{
    /// <summary>Steady-state gain in dB, measured over the second half of a one-second tone.</summary>
    private static double GainDb(double frequency)
    {
        var tone = Signals.Tone(1000, peakDbfs: -12, frequency: frequency);
        var filtered = HighPassFilter.Apply(tone);
        double Rms(short[] x) => Math.Sqrt(x.Skip(x.Length / 2).Average(s => (double)s * s));
        return 20 * Math.Log10(Rms(filtered.Samples) / Rms(tone.Samples));
    }

    [Theory]
    [InlineData(25, -38)]
    [InlineData(50, -15)]
    public void Rumble_and_mains_hum_are_strongly_attenuated(double frequency, double atMostDb)
    {
        Assert.True(GainDb(frequency) < atMostDb, $"{frequency} Hz: {GainDb(frequency):0.0} dB");
    }

    [Theory]
    [InlineData(120)]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(3000)]
    public void The_voice_range_passes_unchanged(double frequency)
    {
        Assert.InRange(GainDb(frequency), -0.3, 0.1);
    }

    [Fact]
    public void A_dc_offset_is_removed_without_a_click()
    {
        var filtered = HighPassFilter.Apply(Signals.Constant(500, 4000));
        Assert.All(filtered.Samples, s => Assert.InRange((int)s, -1, 1));
    }

    [Fact]
    public void Length_and_rate_are_preserved_and_input_is_not_modified()
    {
        var tone = Signals.Tone(300, rate: 22050);
        var copy = (short[])tone.Samples.Clone();

        var filtered = HighPassFilter.Apply(tone);

        Assert.Equal(22050, filtered.SampleRate);
        Assert.Equal(tone.SampleCount, filtered.SampleCount);
        Assert.Equal(copy, tone.Samples);
    }

    [Fact]
    public void Empty_audio_or_a_zero_cutoff_returns_the_input()
    {
        var empty = new PcmAudio(44100, []);
        var tone = Signals.Tone(100);
        Assert.Same(empty, HighPassFilter.Apply(empty));
        Assert.Same(tone, HighPassFilter.Apply(tone, cutoffHz: 0));
    }
}
