using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using VoiceClock.Core.Audio;
using VoiceClock.Core.Serialization;
using VoiceClock.Core.Time;

namespace VoiceClock.Core.Storage;

/// <summary>
/// The voice pack (.zip) format, used for backups and for the default voice bundled in the APK:
/// <c>voicepack.json</c> plus <c>clips/&lt;asset id&gt;.wav</c> for each recording.
/// </summary>
public static class VoicePackFormat
{
    public const string FormatId = "egyptian-voice-clock/voice-pack";
    public const int SchemaVersion = 1;
    public const string ManifestEntryName = "voicepack.json";
    public const string ClipFolder = "clips/";
    public const string FileExtension = ".zip";
    public const string MimeType = "application/zip";

    /// <summary>File name of the default voice inside the app package (Resources/Raw).</summary>
    public const string BundledFileName = "voicepack.zip";

    public const long MaxPackBytes = 200L * 1024 * 1024;
    public const long MaxTotalUncompressedBytes = 300L * 1024 * 1024;
    public const long MaxClipBytes = 8L * 1024 * 1024;
    public const long MaxManifestBytes = 1L * 1024 * 1024;
    public const int MaxEntries = 2000;

    public static string ClipEntryName(string assetId) => ClipFolder + assetId + ".wav";

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}

public enum VoicePackProblemKind
{
    NotAZip,
    TooLarge,
    MissingManifest,
    InvalidManifest,
    WrongFormat,
    UnsupportedVersion,
    UnknownClip,
    DuplicateClip,
    UnexpectedPath,
    MissingFile,
    ChecksumMismatch,
    InvalidAudio,
}

public sealed record VoicePackProblem(VoicePackProblemKind Kind, string? ClipId, string Detail)
{
    /// <summary>Fatal problems reject the whole pack; the rest only skip one clip.</summary>
    public bool IsFatal => Kind is VoicePackProblemKind.NotAZip or VoicePackProblemKind.TooLarge or
        VoicePackProblemKind.MissingManifest or VoicePackProblemKind.InvalidManifest or
        VoicePackProblemKind.WrongFormat or VoicePackProblemKind.UnsupportedVersion;
}

/// <summary>One validated clip from a pack (already silence-trimmed).</summary>
public sealed record VoicePackClip(string Id, PcmAudio Audio, string Sha256);

public sealed record VoicePackContents(
    IReadOnlyList<VoicePackClip> Clips,
    IReadOnlyList<VoicePackProblem> Problems,
    DateTimeOffset? CreatedAtUtc,
    string? AppVersion)
{
    public bool IsUsable => !Problems.Any(p => p.IsFatal);

    public static VoicePackContents Rejected(VoicePackProblemKind kind, string detail) =>
        new([], [new VoicePackProblem(kind, null, detail)], null, null);
}

/// <summary>A clip to be written into a pack.</summary>
public sealed record VoicePackSourceClip(string Id, byte[] Wav, string? Text, TimeSpan Duration);

/// <summary>Writes voice packs.</summary>
public static class VoicePackWriter
{
    public static void Write(Stream destination, IEnumerable<VoicePackSourceClip> clips, string? appVersion, string locale, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(clips);
        var list = clips.ToList();

        var manifest = new VoicePackManifestDocument
        {
            Format = VoicePackFormat.FormatId,
            SchemaVersion = VoicePackFormat.SchemaVersion,
            CreatedAtUtc = createdAtUtc,
            AppVersion = appVersion,
            Locale = locale,
            Clips = list.Select(c => (VoicePackClipDocument?)new VoicePackClipDocument
            {
                Id = c.Id,
                File = VoicePackFormat.ClipEntryName(c.Id),
                Sha256 = VoicePackFormat.Sha256Hex(c.Wav),
                DurationMs = (int)c.Duration.TotalMilliseconds,
                Text = c.Text,
            }).ToList(),
        };

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var manifestEntry = archive.CreateEntry(VoicePackFormat.ManifestEntryName, CompressionLevel.Optimal);
        using (var stream = manifestEntry.Open())
        {
            JsonSerializer.Serialize(stream, manifest, CoreJsonContext.Default.VoicePackManifestDocument);
        }

        foreach (var clip in list)
        {
            var entry = archive.CreateEntry(VoicePackFormat.ClipEntryName(clip.Id), CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(clip.Wav);
        }
    }
}

/// <summary>
/// Reads and validates a voice pack. Only entries named exactly <c>clips/&lt;known id&gt;.wav</c>
/// are ever read, sizes are bounded (zip-bomb safe), checksums are verified, and every clip
/// must pass the same audio validation as a fresh recording.
/// </summary>
public static class VoicePackReader
{
    public static VoicePackContents Read(Stream seekableZip, Func<string, bool> isKnownId, ClipProcessingOptions? validation = null)
    {
        ArgumentNullException.ThrowIfNull(seekableZip);
        ArgumentNullException.ThrowIfNull(isKnownId);

        if (seekableZip.CanSeek && seekableZip.Length > VoicePackFormat.MaxPackBytes)
        {
            return VoicePackContents.Rejected(VoicePackProblemKind.TooLarge, "The voice pack is too large.");
        }

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(seekableZip, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            return VoicePackContents.Rejected(VoicePackProblemKind.NotAZip, ex.Message);
        }

        using (archive)
        {
            return ReadArchive(archive, isKnownId, validation ?? ClipProcessingOptions.Default);
        }
    }

    private static VoicePackContents ReadArchive(ZipArchive archive, Func<string, bool> isKnownId, ClipProcessingOptions validation)
    {
        if (archive.Entries.Count > VoicePackFormat.MaxEntries)
        {
            return VoicePackContents.Rejected(VoicePackProblemKind.TooLarge, "The voice pack has too many files.");
        }

        var manifestEntry = archive.GetEntry(VoicePackFormat.ManifestEntryName);
        if (manifestEntry is null)
        {
            return VoicePackContents.Rejected(VoicePackProblemKind.MissingManifest, "voicepack.json is missing.");
        }

        var budget = VoicePackFormat.MaxTotalUncompressedBytes;
        byte[]? manifestBytes;
        try
        {
            manifestBytes = ReadBounded(manifestEntry, VoicePackFormat.MaxManifestBytes, ref budget);
        }
        catch (InvalidDataException ex)
        {
            return VoicePackContents.Rejected(VoicePackProblemKind.InvalidManifest, ex.Message);
        }

        if (manifestBytes is null)
        {
            return VoicePackContents.Rejected(VoicePackProblemKind.TooLarge, "voicepack.json is too large.");
        }

        VoicePackManifestDocument? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBytes, CoreJsonContext.Default.VoicePackManifestDocument);
        }
        catch (JsonException ex)
        {
            return VoicePackContents.Rejected(VoicePackProblemKind.InvalidManifest, ex.Message);
        }

        if (manifest?.Clips is null)
        {
            return VoicePackContents.Rejected(VoicePackProblemKind.InvalidManifest, "voicepack.json has no clip list.");
        }

        if (!string.Equals(manifest.Format, VoicePackFormat.FormatId, StringComparison.Ordinal))
        {
            return VoicePackContents.Rejected(VoicePackProblemKind.WrongFormat, $"Unexpected format '{manifest.Format}'.");
        }

        if (manifest.SchemaVersion is < 1 or > VoicePackFormat.SchemaVersion)
        {
            return VoicePackContents.Rejected(VoicePackProblemKind.UnsupportedVersion, $"Unsupported voice pack version {manifest.SchemaVersion}.");
        }

        var clips = new List<VoicePackClip>();
        var problems = new List<VoicePackProblem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in manifest.Clips)
        {
            var id = document?.Id;
            if (document is null || id is null || !AssetIds.IsWellFormed(id) || !isKnownId(id))
            {
                problems.Add(new VoicePackProblem(VoicePackProblemKind.UnknownClip, id, "Unknown recording id."));
                continue;
            }

            if (!seen.Add(id))
            {
                problems.Add(new VoicePackProblem(VoicePackProblemKind.DuplicateClip, id, "The recording is listed twice."));
                continue;
            }

            var expectedPath = VoicePackFormat.ClipEntryName(id);
            if (!string.Equals(document.File, expectedPath, StringComparison.Ordinal))
            {
                problems.Add(new VoicePackProblem(VoicePackProblemKind.UnexpectedPath, id, $"Expected '{expectedPath}'."));
                continue;
            }

            var entry = archive.GetEntry(expectedPath);
            if (entry is null)
            {
                problems.Add(new VoicePackProblem(VoicePackProblemKind.MissingFile, id, $"'{expectedPath}' is missing."));
                continue;
            }

            byte[]? bytes;
            try
            {
                bytes = ReadBounded(entry, VoicePackFormat.MaxClipBytes, ref budget);
            }
            catch (InvalidDataException)
            {
                problems.Add(new VoicePackProblem(VoicePackProblemKind.InvalidAudio, id, "The recording file is damaged."));
                continue;
            }

            if (bytes is null)
            {
                if (budget <= 0)
                {
                    return new VoicePackContents(
                        [],
                        [.. problems, new VoicePackProblem(VoicePackProblemKind.TooLarge, id, "The voice pack expands to too much data.")],
                        manifest.CreatedAtUtc,
                        manifest.AppVersion);
                }

                problems.Add(new VoicePackProblem(VoicePackProblemKind.InvalidAudio, id, "The recording file is too large."));
                continue;
            }

            var sha = VoicePackFormat.Sha256Hex(bytes);
            if (document.Sha256 is not null && !string.Equals(document.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new VoicePackProblem(VoicePackProblemKind.ChecksumMismatch, id, "The recording is damaged (checksum mismatch)."));
                continue;
            }

            if (!WavCodec.TryDecode(bytes, out var audio, out var audioProblem))
            {
                problems.Add(new VoicePackProblem(VoicePackProblemKind.InvalidAudio, id, $"Unreadable audio ({audioProblem})."));
                continue;
            }

            var processed = ClipProcessor.Process(audio, validation);
            if (!processed.IsValid)
            {
                problems.Add(new VoicePackProblem(VoicePackProblemKind.InvalidAudio, id, $"Unusable recording ({string.Join(", ", processed.Issues)})."));
                continue;
            }

            clips.Add(new VoicePackClip(id, processed.Audio, sha));
        }

        return new VoicePackContents(clips, problems, manifest.CreatedAtUtc, manifest.AppVersion);
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes, regardless of what the zip header claims.
    /// Returns null when the entry is too large; sets <paramref name="budget"/> to 0 when the
    /// whole pack has expanded beyond its total allowance.
    /// </summary>
    private static byte[]? ReadBounded(ZipArchiveEntry entry, long limit, ref long budget)
    {
        if (entry.Length > budget)
        {
            budget = 0;
            return null;
        }

        if (entry.Length > limit)
        {
            return null;
        }

        using var source = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > budget)
            {
                budget = 0;
                return null;
            }

            if (total > limit)
            {
                budget -= total;
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        budget -= total;
        return buffer.ToArray();
    }
}

internal sealed class VoicePackManifestDocument
{
    public string? Format { get; set; }

    public int SchemaVersion { get; set; }

    public DateTimeOffset? CreatedAtUtc { get; set; }

    public string? AppVersion { get; set; }

    public string? Locale { get; set; }

    public List<VoicePackClipDocument?>? Clips { get; set; }
}

internal sealed class VoicePackClipDocument
{
    public string? Id { get; set; }

    public string? File { get; set; }

    public string? Sha256 { get; set; }

    public int DurationMs { get; set; }

    public string? Text { get; set; }
}
