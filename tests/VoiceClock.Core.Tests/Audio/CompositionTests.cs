using VoiceClock.Core.Audio;
using VoiceClock.Core.Tests.TestSupport;

namespace VoiceClock.Core.Tests.Audio;

public class CompositionTests
{
    private static readonly CompositionOptions Plain = new() { NormalizeLoudness = false, GapMs = 50, LeadInMs = 100, TailMs = 200 };

    [Fact]
    public void Clips_are_joined_in_order_with_lead_gaps_and_tail()
    {
        var a = Signals.Constant(100, 1000);
        var b = Signals.Constant(200, 2000);
        var c = Signals.Constant(300, 3000);

        var result = AnnouncementComposer.Compose([a, b, c], Plain);

        var rate = Signals.Rate;
        int Ms(int ms) => rate * ms / 1000;
        Assert.Equal(Ms(100) + Ms(100) + Ms(50) + Ms(200) + Ms(50) + Ms(300) + Ms(200), result.SampleCount);

        var s = result.Samples;
        Assert.Equal(0, s[Ms(100) - 1]);                       // lead-in silence
        Assert.Equal(1000, s[Ms(100)]);                        // clip a starts
        Assert.Equal(0, s[Ms(200) + 5]);                       // gap
        Assert.Equal(2000, s[Ms(250)]);                        // clip b starts
        Assert.Equal(3000, s[Ms(500)]);                        // clip c starts after second gap
        Assert.Equal(0, s[^1]);                                // tail silence
    }

    [Fact]
    public void Clips_never_overlap()
    {
        var result = AnnouncementComposer.Compose([Signals.Constant(100, 1000), Signals.Constant(100, 2000)], Plain with { GapMs = 0 });
        Assert.DoesNotContain((short)3000, result.Samples);
    }

    [Fact]
    public void Different_sample_rates_are_converted_to_the_first_clip_rate()
    {
        var a = Signals.Tone(200, rate: 44100);
        var b = Signals.Tone(200, rate: 22050);

        var result = AnnouncementComposer.Compose([a, b], Plain);

        Assert.Equal(44100, result.SampleRate);
        Assert.InRange(result.Duration.TotalMilliseconds, 100 + 200 + 50 + 200 + 200 - 2, 100 + 200 + 50 + 200 + 200 + 2);
    }

    [Fact]
    public void Composition_is_deterministic()
    {
        var clips = new[] { Signals.Word(), Signals.Word(peakDbfs: -20) };
        var first = AnnouncementComposer.Compose(clips);
        var second = AnnouncementComposer.Compose(clips);
        Assert.Equal(first.Samples, second.Samples);
    }

    [Fact]
    public void An_empty_clip_list_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => AnnouncementComposer.Compose([]));
    }

    [Fact]
    public void Normalization_lifts_quiet_clips_without_exceeding_the_ceiling()
    {
        var quiet = ClipProcessor.Process(Signals.Word(peakDbfs: -30)).Audio;
        var loud = ClipProcessor.Process(Signals.Word(peakDbfs: -2)).Audio;

        var quietAfter = ClipAnalyzer.Analyze(LoudnessNormalizer.Normalize(quiet));
        var loudAfter = ClipAnalyzer.Analyze(LoudnessNormalizer.Normalize(loud));

        Assert.True(quietAfter.PeakDbfs > -30 + 6, $"quiet clip peak {quietAfter.PeakDbfs}");
        Assert.True(loudAfter.PeakDbfs <= -1 + 0.05, $"loud clip peak {loudAfter.PeakDbfs}");
        Assert.True(Math.Abs(quietAfter.SpeechRmsDbfs - loudAfter.SpeechRmsDbfs) < 6);
    }

    [Fact]
    public void Normalization_never_boosts_more_than_the_limit()
    {
        // Speech peaking at -36 dBFS (about -39 dBFS RMS) would need +19 dB; it gets the 12 dB maximum.
        var veryQuiet = ClipProcessor.Process(Signals.Word(peakDbfs: -36)).Audio;
        var after = ClipAnalyzer.Analyze(LoudnessNormalizer.Normalize(veryQuiet));
        Assert.InRange(after.PeakDbfs, -36 + 11.5, -36 + 12.5);
    }

    [Fact]
    public void Normalization_leaves_silence_untouched()
    {
        var silence = Signals.Silence(300);
        Assert.Same(silence, LoudnessNormalizer.Normalize(silence));
    }

    [Fact]
    public void Resampling_keeps_duration_and_a_constant_level()
    {
        var resampled = AudioMath.Resample(Signals.Constant(500, 1234, rate: 22050), 44100);
        Assert.Equal(44100, resampled.SampleRate);
        Assert.Equal(22050, resampled.SampleCount);
        Assert.All(resampled.Samples, s => Assert.Equal(1234, s));
    }
}
