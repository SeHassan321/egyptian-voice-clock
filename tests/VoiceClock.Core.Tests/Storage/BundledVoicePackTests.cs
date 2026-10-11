using System.Runtime.CompilerServices;
using VoiceClock.Core.Audio;
using VoiceClock.Core.Storage;
using VoiceClock.Core.Tests.TestSupport;
using VoiceClock.Core.Validation;

namespace VoiceClock.Core.Tests.Storage;

/// <summary>
/// Checks the default voice that ships inside the app (src/VoiceClock.App/Resources/Raw/voicepack.zip),
/// so a damaged or incomplete voice update fails the build instead of reaching phones.
/// </summary>
public class BundledVoicePackTests
{
    private static string? FindBundledPack([CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EgyptianVoiceClock.sln")))
        {
            directory = directory.Parent;
        }

        var pack = directory is null ? null : Path.Combine(directory.FullName, "src", "VoiceClock.App", "Resources", "Raw", VoicePackFormat.BundledFileName);
        return pack is not null && File.Exists(pack) ? pack : null;
    }

    [Fact]
    public void The_shipped_voice_is_valid_and_speaks_every_minute_of_the_day()
    {
        var path = FindBundledPack();
        if (path is null)
        {
            return; // building without a default voice is allowed
        }

        using var stream = File.OpenRead(path);
        var contents = VoicePackReader.Read(stream, TestCatalog.Catalog.IsKnownId);

        Assert.True(contents.IsUsable);
        Assert.Empty(contents.Problems);

        var available = contents.Clips.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(TestCatalog.Catalog.RequiredEntries(includeDayPeriod: true), e => Assert.True(available.Contains(e.Id), $"missing {e.Id}"));
        Assert.True(CoverageValidator.Validate(TestCatalog.Engine, available.Contains, includeDayPeriod: true).IsComplete);
    }

    [Fact]
    public void Every_shipped_clip_is_clean_speech()
    {
        var path = FindBundledPack();
        if (path is null)
        {
            return;
        }

        using var stream = File.OpenRead(path);
        foreach (var clip in VoicePackReader.Read(stream, TestCatalog.Catalog.IsKnownId).Clips)
        {
            var analysis = ClipAnalyzer.Analyze(clip.Audio);
            Assert.True(analysis.HasSpeech, clip.Id);
            Assert.True(analysis.ClippedFraction < 0.002, $"{clip.Id} is distorted");
            Assert.InRange(clip.Audio.Duration.TotalSeconds, 0.3, 4.0);

            // After cleaning, no clip starts or ends with a long stretch of silence or noise.
            var lead = analysis.SpeechStartSample / (double)clip.Audio.SampleRate;
            var tail = (clip.Audio.SampleCount - analysis.SpeechEndSample) / (double)clip.Audio.SampleRate;
            Assert.True(lead < 0.1, $"{clip.Id} starts with {lead:0.00}s before the voice");
            Assert.True(tail < 0.25, $"{clip.Id} ends with {tail:0.00}s after the voice");
        }
    }
}
