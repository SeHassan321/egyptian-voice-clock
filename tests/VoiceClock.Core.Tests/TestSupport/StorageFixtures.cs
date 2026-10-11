using System.IO.Compression;
using System.Text.Json.Nodes;
using VoiceClock.Core.Audio;
using VoiceClock.Core.Catalog;
using VoiceClock.Core.Storage;
using VoiceClock.Core.Time;

namespace VoiceClock.Core.Tests.TestSupport;

/// <summary>A unique temporary directory, deleted when disposed.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "voiceclock-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class TestCatalog
{
    public static readonly TimeSpeechEngine Engine = new(SpeechRules.Default);
    public static readonly RecordingCatalog Catalog = new(Engine);
}

/// <summary>Serves an in-memory voice pack as if it were bundled in the APK.</summary>
internal sealed class FakeBundledSource : IBundledVoicePackSource
{
    public FakeBundledSource(byte[]? pack, string versionTag = "1.0 (1)")
    {
        Pack = pack;
        VersionTag = versionTag;
    }

    public byte[]? Pack { get; set; }

    public string VersionTag { get; set; }

    public int OpenCount { get; private set; }

    public Task<Stream?> OpenAsync(CancellationToken cancellationToken)
    {
        OpenCount++;
        return Task.FromResult<Stream?>(Pack is null ? null : new MemoryStream(Pack, writable: false));
    }
}

/// <summary>Builds voice packs, valid or deliberately broken.</summary>
internal static class PackBuilder
{
    /// <summary>A distinct "voice" per id so tests can tell clips apart.</summary>
    public static PcmAudio ClipFor(string id) =>
        ClipProcessor.Process(Signals.Word(speechMs: 300 + (Math.Abs(id.GetHashCode(StringComparison.Ordinal)) % 300), peakDbfs: -10)).Audio;

    public static byte[] Build(IEnumerable<string> ids) => Build(ids.Select(id => (id, ClipFor(id))));

    public static byte[] Build(IEnumerable<(string Id, PcmAudio Audio)> clips)
    {
        using var memory = new MemoryStream();
        VoicePackWriter.Write(
            memory,
            clips.Select(c => new VoicePackSourceClip(c.Id, WavCodec.Encode(c.Audio), null, c.Audio.Duration)),
            "test",
            "ar-EG",
            new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        return memory.ToArray();
    }

    /// <summary>Rewrites the manifest JSON and/or entries of an existing pack.</summary>
    public static byte[] Mutate(byte[] pack, Action<JsonObject>? manifest = null, Action<ZipArchive>? archive = null)
    {
        using var memory = new MemoryStream();
        memory.Write(pack);
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
        {
            if (manifest is not null)
            {
                var entry = zip.GetEntry(VoicePackFormat.ManifestEntryName)!;
                JsonObject root;
                using (var reader = new StreamReader(entry.Open()))
                {
                    root = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
                }

                manifest(root);
                entry.Delete();
                var replacement = zip.CreateEntry(VoicePackFormat.ManifestEntryName);
                using var writer = new StreamWriter(replacement.Open());
                writer.Write(root.ToJsonString());
            }

            archive?.Invoke(zip);
        }

        return memory.ToArray();
    }

    public static void ReplaceEntry(ZipArchive zip, string name, byte[] content)
    {
        zip.GetEntry(name)?.Delete();
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(content);
    }
}
