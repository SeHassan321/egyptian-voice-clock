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
    public void Isolated_clicks_around_the_speech_are_not_taken_for_the_voice()
    {
        var click = Signals.Tone(5, peakDbfs: -1, frequency: 2000);
        var clip = Signals.Concat(
            Signals.Noise(50), click, Signals.Noise(600, seed: 2),
            Signals.Tone(600, peakDbfs: -10),
            Signals.Noise(400, seed: 3), click, Signals.Noise(100, seed: 4));

        var analysis = ClipAnalyzer.Analyze(clip);
        var speechStart = Ms(50 + 5 + 600);

        Assert.InRange(analysis.SpeechStartSample, speechStart - Window, speechStart + Window);
        Assert.InRange(analysis.SpeechEndSample, speechStart + Ms(600) - Window, speechStart + Ms(600) + Window);
    }

    [Fact]
    public void A_lone_click_is_not_speech()
    {
        var clip = Signals.Concat(Signals.Noise(400), Signals.Tone(5, peakDbfs: -1, frequency: 2000), Signals.Noise(400, seed: 9));
        Assert.False(ClipAnalyzer.Analyze(clip).HasSpeech);
    }

    [Fact]
    public void Low_frequency_rumble_before_a_word_is_removed_and_trimmed()
    {
        // 800 ms of 25 Hz + 50 Hz rumble with faint room noise (like the owner's minute recordings), then the word.
        var rumble = Signals.Mix(Signals.Tone(2000, peakDbfs: -26, frequency: 25), Signals.Tone(2000, peakDbfs: -30, frequency: 50));
        var speech = Signals.Concat(Signals.Silence(800), Signals.Tone(700, peakDbfs: -8, frequency: 300), Signals.Silence(500));
        var clip = Signals.Mix(Signals.Mix(rumble, speech), Signals.Noise(2000, peakDbfs: -62));

        var result = ClipProcessor.Process(clip);

        Assert.True(result.IsValid);
        var expected = Ms(700 + ClipProcessingOptions.Default.LeadPaddingMs + ClipProcessingOptions.Default.TailPaddingMs);
        Assert.InRange(result.Audio.SampleCount, expected - (3 * Window), expected + (3 * Window));
        Assert.True(ClipAnalyzer.Analyze(result.Audio.Slice(Ms(5), Ms(30))).PeakDbfs < -40, "rumble left in the lead-in");
    }

    [Fact]
    public void A_soft_hissing_consonant_at_the_start_of_a_word_is_kept()
    {
        // "صباحًا": ~120 ms of quiet hiss before the vowel, over rumble that is louder than the hiss.
        var rumble = Signals.Tone(2000, peakDbfs: -30, frequency: 40);
        var word = Signals.Concat(Signals.Silence(600), Signals.Hiss(120, rmsDbfs: -40), Signals.Tone(500, peakDbfs: -8, frequency: 250), Signals.Silence(780));
        var clip = Signals.Mix(rumble, word);

        var analysis = ClipAnalyzer.Analyze(HighPassFilter.Apply(clip));

        Assert.True(analysis.HasSpeech);
        Assert.InRange(analysis.SpeechStartSample, Ms(600) - Window, Ms(600) + (2 * Window));
    }

    [Fact]
    public void A_soft_hissing_consonant_at_the_end_of_a_word_is_kept()
    {
        // "ونص": vowel then ~150 ms of quiet hiss.
        var clip = Signals.Concat(Signals.Silence(300), Signals.Tone(400, peakDbfs: -8, frequency: 250), Signals.Hiss(150, rmsDbfs: -42), Signals.Silence(400));

        var analysis = ClipAnalyzer.Analyze(clip);

        Assert.InRange(analysis.SpeechEndSample, Ms(850) - (2 * Window), Ms(850) + Window);
    }

    [Fact]
    public void Speech_level_ignores_the_quiet_consonants()
    {
        var vowelOnly = ClipAnalyzer.Analyze(Signals.Concat(Signals.Silence(200), Signals.Tone(500, peakDbfs: -8), Signals.Silence(200)));
        var withHiss = ClipAnalyzer.Analyze(Signals.Concat(Signals.Silence(200), Signals.Hiss(150, rmsDbfs: -40), Signals.Tone(500, peakDbfs: -8), Signals.Silence(200)));

        Assert.Equal(vowelOnly.SpeechRmsDbfs, withHiss.SpeechRmsDbfs, tolerance: 0.3);
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
        var clip = Signals.Concat(Signals.Noise(300), Signals.Tone(600, peakDbfs: -10), Signals.Noise(300, seed: 3));
        var result = ClipProcessor.Process(clip);

        // Every loud sample of the (filtered) recording survives the trim.
        var filtered = HighPassFilter.Apply(clip);
        var loudInOriginal = filtered.Samples.Count(s => Math.Abs((int)s) > 3000);
        var loudAfter = result.Audio.Samples.Count(s => Math.Abs((int)s) > 3000);
        Assert.True(loudInOriginal > 20000);
        Assert.Equal(loudInOriginal, loudAfter);
    }

    [Fact]
    public void Fades_remove_clicks_at_both_edges()
    {
        // A loud tone that starts and stops abruptly, with no silence around it.
        var result = ClipProcessor.Process(Signals.Tone(300, peakDbfs: -3, frequency: 200));

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
