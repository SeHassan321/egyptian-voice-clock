using System.Buffers.Binary;
using System.Text.Json;
using VoiceClock.Core.Audio;
using VoiceClock.Core.Catalog;
using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.Serialization;
using VoiceClock.Core.Time;

namespace VoiceClock.Core.Storage;

/// <summary>
/// File-based store for all recordings.
/// <list type="bullet">
/// <item>Own recordings live in <c>voice/user/&lt;id&gt;.wav</c>, described by <c>voice/manifest.json</c>.</item>
/// <item>The default voice is extracted once per app build from the bundled voice pack into <c>voice/bundled/</c>.</item>
/// <item>An own recording always wins over the default; deleting it restores the default.</item>
/// <item>Every write is atomic, and the manifest is reconciled with the files at start-up, so an
/// interrupted save never destroys a previously valid recording.</item>
/// </list>
/// </summary>
public sealed class VoiceAssetRepository : IVoiceAssetRepository
{
    public const int ManifestSchemaVersion = 1;
    private const string BundledIndexFile = "index.json";
    private const string BundledTempPrefix = "bundled.tmp-";

    private readonly StorageLocations _locations;
    private readonly RecordingCatalog _catalog;
    private readonly IBundledVoicePackSource _bundledSource;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, ClipFile> _user = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipFile> _bundled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PcmAudio> _cache = new(StringComparer.Ordinal);
    private VoiceManifestDocument _manifest = new();
    private BundledVoiceInfo _bundledInfo = BundledVoiceInfo.None;
    private Task? _initialization;

    public VoiceAssetRepository(StorageLocations locations, RecordingCatalog catalog, IBundledVoicePackSource bundledSource, IAppLog? log = null)
    {
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _bundledSource = bundledSource ?? throw new ArgumentNullException(nameof(bundledSource));
        _log = log ?? NullAppLog.Instance;
    }

    public event EventHandler<AssetsChangedEventArgs>? Changed;

    public bool IsInitialized
    {
        get
        {
            lock (_sync)
            {
                return _initialization is { IsCompletedSuccessfully: true };
            }
        }
    }

    public BundledVoiceInfo BundledVoice
    {
        get
        {
            lock (_sync)
            {
                return _bundledInfo;
            }
        }
    }

    public IReadOnlyList<string> AvailableFullPhraseIds
    {
        get
        {
            lock (_sync)
            {
                return _user.Where(p => !p.Value.Invalid).Select(p => p.Key)
                    .Concat(_bundled.Where(p => !p.Value.Invalid).Select(p => p.Key))
                    .Where(AssetIds.IsFullPhrase)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();
            }
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task initialization;
        lock (_sync)
        {
            if (_initialization is null || _initialization.IsFaulted || _initialization.IsCanceled)
            {
                _initialization = Task.Run(InitializeCoreAsync, CancellationToken.None);
            }

            initialization = _initialization;
        }

        return initialization.WaitAsync(cancellationToken);
    }

    public AssetStatus GetStatus(string assetId)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        lock (_sync)
        {
            _user.TryGetValue(assetId, out var user);
            _bundled.TryGetValue(assetId, out var bundled);
            var hasUser = user is not null;
            var hasBundled = bundled is { Invalid: false };

            if (user is { Invalid: false })
            {
                return new AssetStatus(assetId, AssetState.Ready, AssetSource.User, hasUser, hasBundled, user.Duration, null);
            }

            if (bundled is { Invalid: false })
            {
                return new AssetStatus(assetId, AssetState.Ready, AssetSource.Bundled, hasUser, hasBundled, bundled.Duration, user?.Problem);
            }

            if (user is not null)
            {
                return new AssetStatus(assetId, AssetState.Invalid, AssetSource.User, hasUser, hasBundled, null, user.Problem);
            }

            if (bundled is not null)
            {
                return new AssetStatus(assetId, AssetState.Invalid, AssetSource.Bundled, hasUser, hasBundled, null, bundled.Problem);
            }

            return new AssetStatus(assetId, AssetState.Missing, AssetSource.None, false, false, null, null);
        }
    }

    public bool IsAvailable(string assetId) => GetStatus(assetId).IsReady;

    public async Task<PcmAudio?> LoadAsync(string assetId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        ClipFile? user, bundled;
        lock (_sync)
        {
            if (_cache.TryGetValue(assetId, out var cached))
            {
                return cached;
            }

            _user.TryGetValue(assetId, out user);
            _bundled.TryGetValue(assetId, out bundled);
        }

        foreach (var (clip, source) in new[] { (user, AssetSource.User), (bundled, AssetSource.Bundled) })
        {
            if (clip is null || clip.Invalid)
            {
                continue;
            }

            var audio = await TryReadClipAsync(assetId, clip, cancellationToken).ConfigureAwait(false);
            if (audio is null)
            {
                RaiseChanged([assetId]);
                continue;
            }

            if (clip.ProcessingVersion < ClipProcessor.Version)
            {
                var processed = ClipProcessor.Process(audio);
                if (processed.IsValid)
                {
                    audio = processed.Audio;
                }
            }

            lock (_sync)
            {
                // Only cache if nothing replaced this clip while it was being read.
                var stillEffective = source == AssetSource.User
                    ? _user.TryGetValue(assetId, out var current) && ReferenceEquals(current, clip)
                    : !_user.TryGetValue(assetId, out var newer) || newer.Invalid;
                if (stillEffective)
                {
                    _cache[assetId] = audio;
                }
            }

            return audio;
        }

        return null;
    }

    public async Task SaveUserClipAsync(string assetId, PcmAudio audio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var entry = _catalog.Find(assetId) ?? throw new ArgumentException($"Unknown recording id '{assetId}'.", nameof(assetId));
        if (audio.IsEmpty)
        {
            throw new ArgumentException("Cannot save an empty recording.", nameof(audio));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var wav = WavCodec.Encode(audio);
        var sha = VoicePackFormat.Sha256Hex(wav);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = UserPath(assetId);
            await AtomicFile.WriteAllBytesAsync(path, wav, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _user[assetId] = new ClipFile(path, audio.Duration, sha) { ProcessingVersion = ClipProcessor.Version };
                _cache[assetId] = audio;
                _manifest.Clips[assetId] = NewManifestEntry(entry, path, audio.Duration, sha, DateTimeOffset.UtcNow);
            }

            await WriteManifestAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        RaiseChanged([assetId]);
    }

    public async Task<bool> DeleteUserClipAsync(string assetId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        bool existed;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool manifestChanged;
            lock (_sync)
            {
                existed = _user.Remove(assetId);
                _cache.Remove(assetId);
                manifestChanged = _manifest.Clips.Remove(assetId);
            }

            var path = UserPath(assetId);
            if (File.Exists(path))
            {
                File.Delete(path);
                existed = true;
            }

            if (manifestChanged)
            {
                await WriteManifestAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        if (existed)
        {
            RaiseChanged([assetId]);
        }

        return existed;
    }

    public async Task<VerificationResult> VerifyAllAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        List<(string Id, ClipFile Clip)> snapshot;
        lock (_sync)
        {
            snapshot = _user.Select(p => (p.Key, p.Value)).Concat(_bundled.Select(p => (p.Key, p.Value))).ToList();
        }

        var invalid = new List<string>();
        foreach (var (id, clip) in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clip.Invalid || await TryReadClipAsync(id, clip, cancellationToken).ConfigureAwait(false) is null)
            {
                invalid.Add(id);
            }
        }

        if (invalid.Count > 0)
        {
            lock (_sync)
            {
                foreach (var id in invalid)
                {
                    _cache.Remove(id);
                }
            }

            RaiseChanged(invalid.Distinct(StringComparer.Ordinal).ToList());
        }

        return new VerificationResult(snapshot.Count, invalid.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
    }

    public async Task<int> ExportAsync(Stream destination, VoicePackScope scope, string? appVersion = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        List<(string Id, ClipFile Clip)> candidates;
        lock (_sync)
        {
            var ids = scope == VoicePackScope.UserRecordings
                ? _user.Where(p => !p.Value.Invalid).Select(p => p.Key)
                : _user.Where(p => !p.Value.Invalid).Select(p => p.Key).Concat(_bundled.Where(p => !p.Value.Invalid).Select(p => p.Key));

            candidates = ids.Distinct(StringComparer.Ordinal)
                .Select(id => (id, _user.TryGetValue(id, out var u) && !u.Invalid ? u : _bundled[id]))
                .OrderBy(p => _catalog.Find(p.id)?.Order ?? int.MaxValue)
                .ThenBy(p => p.id, StringComparer.Ordinal)
                .ToList();
        }

        var clips = new List<VoicePackSourceClip>(candidates.Count);
        foreach (var (id, clip) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(clip.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Skipping unreadable clip {id} during export.", ex);
                continue;
            }

            if (!WavCodec.TryDecode(bytes, out var audio, out _))
            {
                _log.Warn($"Skipping damaged clip {id} during export.");
                continue;
            }

            clips.Add(new VoicePackSourceClip(id, bytes, _catalog.Find(id)?.Text, audio.Duration));
        }

        using var memory = new MemoryStream();
        VoicePackWriter.Write(memory, clips, appVersion, _catalog.Locale, DateTimeOffset.UtcNow);
        memory.Position = 0;
        await memory.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return clips.Count;
    }

    public async Task<VoicePackImportResult> ImportAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var memory = await CopyToMemoryAsync(source, VoicePackFormat.MaxPackBytes, cancellationToken).ConfigureAwait(false);
        if (memory is null)
        {
            return VoicePackImportResult.Rejected([new VoicePackProblem(VoicePackProblemKind.TooLarge, null, "The voice pack is too large.")]);
        }

        VoicePackContents contents;
        using (memory)
        {
            contents = VoicePackReader.Read(memory, _catalog.IsKnownId);
        }

        if (!contents.IsUsable)
        {
            return VoicePackImportResult.Rejected(contents.Problems);
        }

        int imported = 0, matchedDefault = 0;
        var changed = new List<string>();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var clip in contents.Clips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = _catalog.Find(clip.Id)!;

                ClipFile? bundled;
                lock (_sync)
                {
                    _bundled.TryGetValue(clip.Id, out bundled);
                }

                if (bundled is { Invalid: false } && string.Equals(bundled.Sha256, clip.Sha256, StringComparison.Ordinal))
                {
                    // Identical to the shipped default: keep using the default.
                    var userPath = UserPath(clip.Id);
                    AtomicFile.TryDelete(userPath);
                    lock (_sync)
                    {
                        _user.Remove(clip.Id);
                        _manifest.Clips.Remove(clip.Id);
                        _cache.Remove(clip.Id);
                    }

                    matchedDefault++;
                    changed.Add(clip.Id);
                    continue;
                }

                var wav = WavCodec.Encode(clip.Audio);
                var sha = VoicePackFormat.Sha256Hex(wav);
                var path = UserPath(clip.Id);
                await AtomicFile.WriteAllBytesAsync(path, wav, CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    _user[clip.Id] = new ClipFile(path, clip.Audio.Duration, sha) { ProcessingVersion = ClipProcessor.Version };
                    _cache[clip.Id] = clip.Audio;
                    _manifest.Clips[clip.Id] = NewManifestEntry(entry, path, clip.Audio.Duration, sha, DateTimeOffset.UtcNow);
                }

                imported++;
                changed.Add(clip.Id);
            }
        }
        finally
        {
            try
            {
                await WriteManifestAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        if (changed.Count > 0)
        {
            RaiseChanged(changed);
        }

        return new VoicePackImportResult(true, imported, matchedDefault, contents.Problems);
    }

    private async Task InitializeCoreAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_locations.UserClipsDirectory);
            AtomicFile.DeleteTempFiles(_locations.VoiceDirectory);
            AtomicFile.DeleteTempFiles(_locations.UserClipsDirectory);
            DeleteStaleBundledTempDirectories();

            await LoadBundledAsync().ConfigureAwait(false);
            await LoadUserClipsAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        RaiseChanged(null);
    }

    private async Task LoadBundledAsync()
    {
        var tag = _bundledSource.VersionTag ?? string.Empty;
        var directory = _locations.BundledClipsDirectory;
        var index = ReadBundledIndex(Path.Combine(directory, BundledIndexFile));

        if (index is not null && string.Equals(index.Tag, tag, StringComparison.Ordinal) && index.ProcessingVersion == ClipProcessor.Version)
        {
            var clips = new Dictionary<string, ClipFile>(StringComparer.Ordinal);
            foreach (var (id, sha) in index.Clips)
            {
                var path = Path.Combine(directory, id + ".wav");
                if (_catalog.IsKnownId(id) && File.Exists(path))
                {
                    clips[id] = InspectFile(path, sha, ClipProcessor.Version);
                }
            }

            lock (_sync)
            {
                ReplaceBundled(clips);
                _bundledInfo = new BundledVoiceInfo(clips.Count > 0, clips.Count, index.Problem);
            }

            return;
        }

        await ExtractBundledAsync(tag).ConfigureAwait(false);
    }

    private async Task ExtractBundledAsync(string tag)
    {
        var directory = _locations.BundledClipsDirectory;
        var temp = Path.Combine(_locations.VoiceDirectory, BundledTempPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var shas = new Dictionary<string, string>(StringComparer.Ordinal);
        string? problem = null;

        try
        {
            Stream? stream = null;
            try
            {
                stream = await _bundledSource.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _log.Warn("Could not open the bundled voice pack.", ex);
                problem = "The bundled voice pack could not be opened.";
            }

            if (stream is not null)
            {
                MemoryStream? memory;
                await using (stream.ConfigureAwait(false))
                {
                    memory = await CopyToMemoryAsync(stream, VoicePackFormat.MaxPackBytes, CancellationToken.None).ConfigureAwait(false);
                }

                if (memory is null)
                {
                    problem = "The bundled voice pack is too large.";
                }
                else
                {
                    VoicePackContents contents;
                    using (memory)
                    {
                        contents = VoicePackReader.Read(memory, _catalog.IsKnownId);
                    }

                    if (!contents.IsUsable)
                    {
                        problem = contents.Problems.First(p => p.IsFatal).Detail;
                    }

                    foreach (var p in contents.Problems.Where(p => !p.IsFatal))
                    {
                        _log.Warn($"Bundled voice pack: {p.Kind} {p.ClipId}: {p.Detail}");
                    }

                    foreach (var clip in contents.Clips)
                    {
                        var wav = WavCodec.Encode(clip.Audio);
                        await File.WriteAllBytesAsync(Path.Combine(temp, clip.Id + ".wav"), wav).ConfigureAwait(false);
                        shas[clip.Id] = VoicePackFormat.Sha256Hex(wav);
                    }
                }
            }

            var indexJson = JsonSerializer.Serialize(
                new BundledIndexDocument { Tag = tag, ProcessingVersion = ClipProcessor.Version, Problem = problem, Clips = shas },
                CoreJsonContext.Default.BundledIndexDocument);
            await File.WriteAllTextAsync(Path.Combine(temp, BundledIndexFile), indexJson).ConfigureAwait(false);

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            Directory.Move(temp, directory);
        }
        catch
        {
            TryDeleteDirectory(temp);
            throw;
        }

        var clips = shas.ToDictionary(
            p => p.Key,
            p => InspectFile(Path.Combine(directory, p.Key + ".wav"), p.Value, ClipProcessor.Version),
            StringComparer.Ordinal);

        lock (_sync)
        {
            ReplaceBundled(clips);
            _bundledInfo = new BundledVoiceInfo(clips.Count > 0, clips.Count, problem);
        }

        _log.Info($"Bundled voice prepared: {clips.Count} clips.");
    }

    private async Task LoadUserClipsAsync()
    {
        var manifest = ReadManifest(out var manifestNeedsWrite);
        var clips = new Dictionary<string, ClipFile>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(_locations.UserClipsDirectory, "*.wav"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var entry = _catalog.Find(id);
            if (entry is null)
            {
                // Unknown to this version (perhaps from a newer one): leave the file untouched.
                continue;
            }

            manifest.Clips.TryGetValue(id, out var manifestEntry);
            var clip = InspectFile(path, manifestEntry?.Sha256, manifestEntry?.ProcessingVersion ?? 0);
            clips[id] = clip;

            if (manifestEntry is null)
            {
                manifest.Clips[id] = NewManifestEntry(entry, path, clip.Duration ?? TimeSpan.Zero, null, File.GetLastWriteTimeUtc(path), processingVersion: 0);
                manifestNeedsWrite = true;
            }

            var status = clip.Invalid ? ManifestStatusInvalid : ManifestStatusRecorded;
            if (!string.Equals(manifest.Clips[id].Status, status, StringComparison.Ordinal))
            {
                manifest.Clips[id].Status = status;
                manifestNeedsWrite = true;
            }
        }

        foreach (var orphan in manifest.Clips.Keys.Where(id => !clips.ContainsKey(id)).ToList())
        {
            manifest.Clips.Remove(orphan);
            manifestNeedsWrite = true;
        }

        lock (_sync)
        {
            _user.Clear();
            foreach (var (id, clip) in clips)
            {
                _user[id] = clip;
                _cache.Remove(id);
            }

            _manifest = manifest;
        }

        if (manifestNeedsWrite)
        {
            await WriteManifestAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private VoiceManifestDocument ReadManifest(out bool needsWrite)
    {
        needsWrite = false;
        var path = _locations.ManifestPath;
        if (!File.Exists(path))
        {
            needsWrite = true;
            return new VoiceManifestDocument();
        }

        try
        {
            var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.VoiceManifestDocument);
            if (manifest is null || manifest.SchemaVersion > ManifestSchemaVersion)
            {
                _log.Warn("Voice manifest is empty or from a newer version; rebuilding it from the files.");
                needsWrite = true;
                return new VoiceManifestDocument();
            }

            if (manifest.SchemaVersion < ManifestSchemaVersion)
            {
                manifest.SchemaVersion = ManifestSchemaVersion;
                needsWrite = true;
            }

            manifest.Clips = new Dictionary<string, VoiceManifestEntry>(
                manifest.Clips.Where(p => p.Value is not null),
                StringComparer.Ordinal);
            return manifest;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warn("Voice manifest is damaged; rebuilding it from the files.", ex);
            var backup = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                File.Move(path, backup, overwrite: true);
            }
            catch (IOException)
            {
            }

            needsWrite = true;
            return new VoiceManifestDocument();
        }
    }

    private async Task WriteManifestAsync(CancellationToken cancellationToken)
    {
        string json;
        lock (_sync)
        {
            json = JsonSerializer.Serialize(_manifest, CoreJsonContext.Default.VoiceManifestDocument);
        }

        await AtomicFile.WriteAllTextAsync(_locations.ManifestPath, json, cancellationToken).ConfigureAwait(false);
    }

    private const string ManifestStatusRecorded = "recorded";
    private const string ManifestStatusInvalid = "invalid";

    private VoiceManifestEntry NewManifestEntry(CatalogEntry entry, string path, TimeSpan duration, string? sha, DateTimeOffset recordedAt, int processingVersion = ClipProcessor.Version) => new()
    {
        ProcessingVersion = processingVersion,
        File = Path.GetRelativePath(_locations.VoiceDirectory, path).Replace('\\', '/'),
        Category = entry.Category.ToString(),
        Status = ManifestStatusRecorded,
        RecordedAtUtc = recordedAt,
        DurationMs = (int)duration.TotalMilliseconds,
        Sha256 = sha,
    };

    private async Task<PcmAudio?> TryReadClipAsync(string assetId, ClipFile clip, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(clip.Path, cancellationToken).ConfigureAwait(false);
            if (WavCodec.TryDecode(bytes, out var audio, out var problem))
            {
                return audio;
            }

            MarkInvalid(assetId, clip, $"Damaged audio ({problem}).");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            MarkInvalid(assetId, clip, "The recording file is missing.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not read clip {assetId}.", ex);
            MarkInvalid(assetId, clip, "The recording file could not be read.");
        }

        return null;
    }

    private void MarkInvalid(string assetId, ClipFile clip, string problem)
    {
        lock (_sync)
        {
            clip.Invalid = true;
            clip.Problem = problem;
            _cache.Remove(assetId);
        }

        _log.Warn($"Clip {assetId} is unusable: {problem}");
    }

    /// <summary>Cheap start-up check: size and RIFF/WAVE header only. Full decoding happens on use.</summary>
    private static ClipFile InspectFile(string path, string? sha, int processingVersion)
    {
        try
        {
            var length = new FileInfo(path).Length;
            Span<byte> header = stackalloc byte[WavCodec.HeaderSize];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (length <= WavCodec.HeaderSize || stream.ReadAtLeast(header, WavCodec.HeaderSize, throwOnEndOfStream: false) < WavCodec.HeaderSize)
                {
                    return new ClipFile(path, null, sha) { Invalid = true, Problem = "The recording is empty." };
                }
            }

            if (!header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
            {
                return new ClipFile(path, null, sha) { Invalid = true, Problem = "The recording is damaged." };
            }

            TimeSpan? duration = null;
            if (header.Slice(36, 4).SequenceEqual("data"u8))
            {
                var rate = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(header[22..]);
                var dataBytes = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header[40..]), length - WavCodec.HeaderSize);
                if (rate > 0 && channels > 0)
                {
                    duration = TimeSpan.FromSeconds(dataBytes / (2.0 * channels * rate));
                }
            }

            return new ClipFile(path, duration, sha) { ProcessingVersion = processingVersion };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ClipFile(path, null, sha) { Invalid = true, Problem = "The recording file could not be read." };
        }
    }

    private BundledIndexDocument? ReadBundledIndex(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.BundledIndexDocument);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warn("Bundled voice index is damaged; extracting again.", ex);
            return null;
        }
    }

    private void ReplaceBundled(Dictionary<string, ClipFile> clips)
    {
        foreach (var id in _bundled.Keys)
        {
            _cache.Remove(id);
        }

        _bundled.Clear();
        foreach (var (id, clip) in clips)
        {
            _bundled[id] = clip;
            _cache.Remove(id);
        }
    }

    private void DeleteStaleBundledTempDirectories()
    {
        if (!Directory.Exists(_locations.VoiceDirectory))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(_locations.VoiceDirectory, BundledTempPrefix + "*"))
        {
            TryDeleteDirectory(dir);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<MemoryStream?> CopyToMemoryAsync(Stream source, long limit, CancellationToken cancellationToken)
    {
        var memory = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                await memory.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            memory.Write(buffer, 0, read);
        }

        memory.Position = 0;
        return memory;
    }

    private string UserPath(string assetId) => Path.Combine(_locations.UserClipsDirectory, assetId + ".wav");

    private void RaiseChanged(IReadOnlyCollection<string>? ids) => Changed?.Invoke(this, new AssetsChangedEventArgs(ids));

    private sealed class ClipFile
    {
        public ClipFile(string path, TimeSpan? duration, string? sha256)
        {
            Path = path;
            Duration = duration;
            Sha256 = sha256;
        }

        /// <summary>The <see cref="ClipProcessor.Version"/> the stored audio was cleaned with (0 = unknown/old).</summary>
        public int ProcessingVersion { get; init; }

        public string Path { get; }

        public TimeSpan? Duration { get; }

        public string? Sha256 { get; }

        public bool Invalid { get; set; }

        public string? Problem { get; set; }
    }
}

internal sealed class VoiceManifestDocument
{
    public int SchemaVersion { get; set; } = VoiceAssetRepository.ManifestSchemaVersion;

    public Dictionary<string, VoiceManifestEntry> Clips { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class VoiceManifestEntry
{
    public string File { get; set; } = string.Empty;

    /// <summary>Processing version of the stored audio; 0 when written by a build without this field.</summary>
    public int ProcessingVersion { get; set; }

    public string Category { get; set; } = string.Empty;

    public string Status { get; set; } = "recorded";

    public DateTimeOffset RecordedAtUtc { get; set; }

    public int DurationMs { get; set; }

    public string? Sha256 { get; set; }
}

internal sealed class BundledIndexDocument
{
    public string Tag { get; set; } = string.Empty;

    public int ProcessingVersion { get; set; }

    public string? Problem { get; set; }

    public Dictionary<string, string> Clips { get; set; } = new(StringComparer.Ordinal);
}
