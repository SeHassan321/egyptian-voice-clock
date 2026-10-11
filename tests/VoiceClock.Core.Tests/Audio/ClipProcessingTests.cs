using VoiceClock.Core.Audio;
using VoiceClock.Core.Tests.TestSupport;

namespace VoiceClock.Core.Tests.Audio;

public class ClipProcessingTests
{
    private const int Window = Signals.Rate / 100; // 10 ms

    [Fact]
    public void Analyzer_finds_the_voiced_part()
    {
        var clip = Signals.Word(leadMs: 400, speechMs: 600, tailMs: 500);
        var analysis = ClipAnalyzer.Analyze(clip);

        Assert.True(analysis.HasSpeech);
        Assert.InRange(analysis.SpeechStartSample, Ms(400) - Window, Ms(400) + Window);
        Assert.InRange(analysis.SpeechEndSample, Ms(1000) - Window, Ms(1000) + Window);
        Assert.Equal(-10, analysis.PeakDbfs, tolerance: 0.2);
    }

    [Fact]
    public void Silence_and_faint_hiss_are_not_speech()
    {
        Assert.False(ClipAnalyzer.Analyze(Signals.Silence(1000)).HasSpeech);
        Assert.False(ClipAnalyzer.Analyze(Signals.Noise(1000, peakDbfs: -55)).HasSpeech);
    }

    [Fact]
    public void Processing_trims_silence_but_keeps_padding_around_the_speech()
    {
        var options = ClipProcessingOptions.Default;
        var result = ClipProcessor.Process(Signals.Word(leadMs: 400, speechMs: 600, tailMs: 500), options);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        var expected = Ms(600 + options.LeadPaddingMs + options.TailPaddingMs);
        Assert.InRange(result.Audio.SampleCount, expected - (2 * Window), expected + (2 * Window));
    }

    [Fact]
    public void Processing_never_cuts_into_the_speech_itself()
    {
        var speech = Signals.Tone(600, peakDbfs: -10);
        var result = ClipProcessor.Process(Signals.Concat(Signals.Noise(300), speech, Signals.Noise(300, seed: 3)));

        // Every loud sample of the original speech survives the trim.
        var loudInOriginal = speech.Samples.Count(s => Math.Abs((int)s) > 3000);
        var loudAfter = result.Audio.Samples.Count(s => Math.Abs((int)s) > 3000);
        Assert.Equal(loudInOriginal, loudAfter);
    }

    [Fact]
    public void Fades_remove_clicks_at_both_edges()
    {
        var result = ClipProcessor.Process(Signals.Concat(Signals.Constant(300, 12000), Signals.Silence(10)));

        Assert.Equal(0, result.Audio.Samples[0]);
        Assert.True(Math.Abs((int)result.Audio.Samples[^1]) < 100);
    }

    [Fact]
    public void Silent_recording_is_invalid()
    {
        var result = ClipProcessor.Process(Signals.Noise(1500, peakDbfs: -58));

        Assert.False(result.IsValid);
        Assert.Equal(new[] { ClipIssue.NoSpeech }, result.Issues.ToArray());
    }

    [Fact]
    public void Empty_recording_is_invalid()
    {
        var result = ClipProcessor.Process(new PcmAudio(44100, []));
        Assert.Equal(new[] { ClipIssue.Empty }, result.Issues.ToArray());
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Very_short_sound_is_invalid()
    {
        var result = ClipProcessor.Process(Signals.Concat(Signals.Silence(300), Signals.Tone(80), Signals.Silence(300)));
        Assert.Contains(ClipIssue.TooShort, result.Issues);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Overlong_recording_is_invalid()
    {
        var result = ClipProcessor.Process(Signals.Tone(13_000));
        Assert.Contains(ClipIssue.TooLong, result.Issues);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Quiet_recording_is_valid_with_a_warning()
    {
        var result = ClipProcessor.Process(Signals.Word(peakDbfs: -32));
        Assert.True(result.IsValid);
        Assert.Equal(new[] { ClipIssue.TooQuiet }, result.Warnings.ToArray());
    }

    [Fact]
    public void Clipped_recording_is_valid_with_a_warning()
    {
        var clipped = Signals.Concat(Signals.Silence(200), Signals.Tone(500, peakDbfs: 6), Signals.Silence(200));
        var result = ClipProcessor.Process(clipped);
        Assert.True(result.IsValid);
        Assert.Contains(ClipIssue.Clipped, result.Warnings);
    }

    [Fact]
    public void Trimming_can_be_disabled()
    {
        var raw = Signals.Word(leadMs: 400, speechMs: 600, tailMs: 500);
        var result = ClipProcessor.Process(raw, new ClipProcessingOptions { TrimSilence = false });
        Assert.Equal(raw.SampleCount, result.Audio.SampleCount);
    }

    [Fact]
    public void Processing_does_not_modify_the_input()
    {
        var raw = Signals.Word();
        var copy = (short[])raw.Samples.Clone();
        ClipProcessor.Process(raw);
        Assert.Equal(copy, raw.Samples);
    }

    [Fact]
    public void Processing_twice_is_stable()
    {
        var once = ClipProcessor.Process(Signals.Word()).Audio;
        var twice = ClipProcessor.Process(once).Audio;
        Assert.InRange(twice.SampleCount, once.SampleCount - (3 * Window), once.SampleCount);
    }

    private static int Ms(int ms) => Signals.Rate * ms / 1000;
}
