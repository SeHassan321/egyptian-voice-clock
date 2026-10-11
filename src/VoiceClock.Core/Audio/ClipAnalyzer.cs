namespace VoiceClock.Core.Audio;

/// <summary>Measurements of one clip, computed over 10 ms windows.</summary>
/// <param name="Duration">Total length.</param>
/// <param name="PeakDbfs">Loudest single sample.</param>
/// <param name="ClippedFraction">Share of samples at full scale.</param>
/// <param name="HasSpeech">Whether anything clearly louder than the background was found.</param>
/// <param name="SpeechStartSample">First sample of the detected speech.</param>
/// <param name="SpeechEndSample">One past the last sample of the detected speech.</param>
/// <param name="SpeechRmsDbfs">Average level of the speech windows.</param>
/// <param name="NoiseFloorDbfs">Estimated background level.</param>
public sealed record ClipAnalysis(
    TimeSpan Duration,
    double PeakDbfs,
    double ClippedFraction,
    bool HasSpeech,
    int SpeechStartSample,
    int SpeechEndSample,
    double SpeechRmsDbfs,
    double NoiseFloorDbfs)
{
    public int SpeechSampleCount => Math.Max(0, SpeechEndSample - SpeechStartSample);
}

/// <summary>Finds where the voice is inside a recording and how loud it is.</summary>
public static class ClipAnalyzer
{
    public const int WindowMilliseconds = 10;

    /// <summary>The loudest window must reach this level to count as speech.</summary>
    public const double MinimumSpeechLevelDbfs = -42.0;

    /// <summary>Windows below this are always treated as silence.</summary>
    public const double AbsoluteSilenceDbfs = -52.0;

    /// <summary>At least this much audio must stand out from the background.</summary>
    public const int MinimumSpeechWindows = 5;

    /// <summary>Window run length used to tell sustained voice from isolated clicks.</summary>
    private const int SustainWindows = 4;

    private const double PreEmphasis = 0.95;

    /// <summary>Lower edge of the band used to find the voice (rumble and hum lie below it).</summary>
    private const double DetectionBandHz = 300;

    public static ClipAnalysis Analyze(PcmAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var samples = audio.Samples;
        var n = samples.Length;
        if (n == 0)
        {
            return new ClipAnalysis(TimeSpan.Zero, AudioMath.SilenceDbfs, 0, false, 0, 0, AudioMath.SilenceDbfs, AudioMath.SilenceDbfs);
        }

        // Speech is detected in the voice band only: background rumble and hum (mostly below 300 Hz)
        // would otherwise look like quiet speech, and how much of it there is varies from clip to clip.
        var voiceBand = audio.SampleRate > 4 * DetectionBandHz ? HighPassFilter.Apply(audio, DetectionBandHz).Samples : samples;

        var window = Math.Max(1, AudioMath.MsToSamples(audio.SampleRate, WindowMilliseconds));
        var windowCount = (n + window - 1) / window;
        var rms = new double[windowCount];
        var voice = new double[windowCount];
        var hf = new double[windowCount];
        var peak = 0;
        long clipped = 0;
        double previous = samples[0];

        for (var w = 0; w < windowCount; w++)
        {
            var start = w * window;
            var end = Math.Min(n, start + window);
            double sumSquares = 0;
            double voiceSquares = 0;
            double hfSquares = 0;
            for (var i = start; i < end; i++)
            {
                int s = samples[i];
                var magnitude = s == short.MinValue ? 32768 : Math.Abs(s);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }

                if (magnitude >= 32767)
                {
                    clipped++;
                }

                sumSquares += (double)s * s;
                voiceSquares += (double)voiceBand[i] * voiceBand[i];

                // Pre-emphasis keeps the hiss of consonants such as س ص ش ف while suppressing rumble.
                var emphasized = s - (PreEmphasis * previous);
                hfSquares += emphasized * emphasized;
                previous = s;
            }

            rms[w] = Math.Sqrt(sumSquares / (end - start));
            voice[w] = Math.Sqrt(voiceSquares / (end - start));
            hf[w] = Math.Sqrt(hfSquares / (end - start));
        }

        var silence = AudioMath.FromDbfs(AbsoluteSilenceDbfs);
        var (noiseFloor, loudest) = FloorAndPeak(rms);
        var (voiceFloor, voiceLoudest) = FloorAndPeak(voice);
        var (hfFloor, hfLoudest) = FloorAndPeak(hf);

        // A window is "voice" when it is well above the background, but never demand more than
        // 15 dB below the loudest window, so soft word endings are kept.
        var threshold = Math.Max(silence, Math.Min(Math.Max(silence, voiceFloor * 3.0), voiceLoudest * 0.18));

        // Consonant hiss is quiet overall but stands far above low-frequency background noise in the
        // emphasized signal, so it is detected separately (never stricter than 20 dB below its peak).
        var hfThreshold = Math.Max(silence, Math.Min(Math.Max(silence, hfFloor * 3.16), hfLoudest * 0.1));

        var isActive = new bool[windowCount];
        int active = 0, voiced = 0;
        double speechSquares = 0;
        for (var w = 0; w < windowCount; w++)
        {
            var loud = voice[w] >= threshold;
            if (loud || hf[w] >= hfThreshold)
            {
                isActive[w] = true;
                active++;
            }

            // The speech level (used to even out loudness) comes from the voiced core only:
            // windows within 20 dB of the loudest, so breaths and consonants do not skew it.
            if (loud && voice[w] >= voiceLoudest * 0.1)
            {
                voiced++;
                speechSquares += rms[w] * rms[w];
            }
        }

        // Speech starts and ends where the energy is sustained (3 of 4 consecutive windows), so a
        // click when the microphone opens or a tap at the end is not taken for the voice.
        int first = -1, last = -1;
        for (var w = 0; w < windowCount && first < 0; w++)
        {
            if (isActive[w] && CountActive(isActive, w, w + SustainWindows - 1) >= SustainWindows - 1)
            {
                first = w;
            }
        }

        for (var w = windowCount - 1; w >= 0 && last < 0; w--)
        {
            if (isActive[w] && CountActive(isActive, w - SustainWindows + 1, w) >= SustainWindows - 1)
            {
                last = w;
            }
        }

        var hasSpeech = loudest >= AudioMath.FromDbfs(MinimumSpeechLevelDbfs) && active >= MinimumSpeechWindows && first >= 0 && last >= first;
        var speechStart = hasSpeech ? first * window : 0;
        var speechEnd = hasSpeech ? Math.Min(n, (last + 1) * window) : 0;
        var speechRms = hasSpeech && voiced > 0 ? Math.Sqrt(speechSquares / voiced) : 0;

        return new ClipAnalysis(
            audio.Duration,
            AudioMath.ToDbfs(peak),
            (double)clipped / n,
            hasSpeech,
            speechStart,
            speechEnd,
            AudioMath.ToDbfs(speechRms),
            AudioMath.ToDbfs(noiseFloor));
    }

    /// <summary>The background level (10th percentile) and the loudest window.</summary>
    private static (double Floor, double Peak) FloorAndPeak(double[] levels)
    {
        var sorted = (double[])levels.Clone();
        Array.Sort(sorted);
        return (sorted[(int)(sorted.Length * 0.1)], sorted[^1]);
    }

    private static int CountActive(bool[] active, int from, int to)
    {
        var count = 0;
        for (var i = Math.Max(0, from); i <= Math.Min(active.Length - 1, to); i++)
        {
            if (active[i])
            {
                count++;
            }
        }

        return count;
    }
}
