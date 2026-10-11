using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.Serialization;
using VoiceClock.Core.Storage;

namespace VoiceClock.Core.Settings;

public enum SettingsLoadOutcome
{
    Loaded,
    CreatedDefaults,
    Migrated,
    RecoveredFromCorruption,

    /// <summary>The file came from a newer app version; known values were read and the file was left untouched.</summary>
    NewerVersion,
}

public sealed record SettingsLoadResult(AppSettings Settings, SettingsLoadOutcome Outcome, string? CorruptFileBackupPath = null);

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public interface ISettingsRepository
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Versioned JSON settings file with step-by-step migrations. A damaged file is set aside
/// (never silently overwritten) and defaults are used, so the app always starts.
/// </summary>
public sealed class JsonSettingsRepository : ISettingsRepository
{
    private static readonly IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> Migrations = new Dictionary<int, Func<JsonObject, JsonObject>>
    {
        // Version 0 = files written before the schema was versioned: same fields, no version number.
        [0] = node =>
        {
            node["schemaVersion"] = 1;
            return node;
        },
    };

    private readonly StorageLocations _locations;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AppSettings _current = AppSettings.Default;

    public JsonSettingsRepository(StorageLocations locations, IAppLog? log = null)
    {
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        _log = log ?? NullAppLog.Instance;
    }

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current => Volatile.Read(ref _current);

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, result.Settings);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sanitized = settings.Sanitized();

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(sanitized, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, sanitized);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke(this, sanitized);
    }

    private async Task<SettingsLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var path = _locations.SettingsPath;
        if (!File.Exists(path))
        {
            await WriteAsync(AppSettings.Default, cancellationToken).ConfigureAwait(false);
            return new SettingsLoadResult(AppSettings.Default, SettingsLoadOutcome.CreatedDefaults);
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Settings could not be read; using defaults.", ex);
            return new SettingsLoadResult(AppSettings.Default, SettingsLoadOutcome.RecoveredFromCorruption);
        }

        JsonObject node;
        int version;
        try
        {
            node = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("Settings must be a JSON object.");
            version = ReadVersion(node);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return await RecoverAsync(path, ex, cancellationToken).ConfigureAwait(false);
        }

        if (version > AppSettings.CurrentSchemaVersion)
        {
            var newer = TryConvert(node);
            if (newer is null)
            {
                return await RecoverAsync(path, new JsonException("Unreadable newer settings."), cancellationToken).ConfigureAwait(false);
            }

            _log.Warn($"Settings schema {version} is newer than this app ({AppSettings.CurrentSchemaVersion}); reading known values only.");
            return new SettingsLoadResult(newer, SettingsLoadOutcome.NewerVersion);
        }

        var migrated = false;
        while (version < AppSettings.CurrentSchemaVersion)
        {
            if (!Migrations.TryGetValue(version, out var migrate))
            {
                return await RecoverAsync(path, new JsonException($"No migration from settings version {version}."), cancellationToken).ConfigureAwait(false);
            }

            node = migrate(node);
            version++;
            migrated = true;
        }

        var settings = TryConvert(node);
        if (settings is null)
        {
            return await RecoverAsync(path, new JsonException("Settings contain invalid values."), cancellationToken).ConfigureAwait(false);
        }

        if (migrated)
        {
            await WriteAsync(settings, cancellationToken).ConfigureAwait(false);
            return new SettingsLoadResult(settings, SettingsLoadOutcome.Migrated);
        }

        return new SettingsLoadResult(settings, SettingsLoadOutcome.Loaded);
    }

    private static int ReadVersion(JsonObject node)
    {
        var value = node["schemaVersion"];
        if (value is null)
        {
            return 0;
        }

        return value.GetValueKind() == JsonValueKind.Number
            ? value.GetValue<int>()
            : throw new FormatException("schemaVersion must be a number.");
    }

    private static AppSettings? TryConvert(JsonObject node)
    {
        try
        {
            var document = node.Deserialize(CoreJsonContext.Default.AppSettingsDocument);
            if (document is null)
            {
                return null;
            }

            var defaults = AppSettings.Default;
            return new AppSettings
            {
                IncludeDayPeriod = document.IncludeDayPeriod ?? defaults.IncludeDayPeriod,
                SegmentGapMs = document.SegmentGapMs ?? defaults.SegmentGapMs,
                NormalizeLoudness = document.NormalizeLoudness ?? defaults.NormalizeLoudness,
                UseFullPhraseOverrides = document.UseFullPhraseOverrides ?? defaults.UseFullPhraseOverrides,
                PlayErrorFeedback = document.PlayErrorFeedback ?? defaults.PlayErrorFeedback,
            }.Sanitized();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private async Task<SettingsLoadResult> RecoverAsync(string path, Exception reason, CancellationToken cancellationToken)
    {
        _log.Warn("Settings file is damaged; keeping a copy and restoring defaults.", reason);
        var backup = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"settings.corrupt-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.json");

        string? backupPath = null;
        try
        {
            File.Move(path, backup);
            backupPath = backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Could not keep a copy of the damaged settings.", ex);
        }

        await WriteAsync(AppSettings.Default, cancellationToken).ConfigureAwait(false);
        return new SettingsLoadResult(AppSettings.Default, SettingsLoadOutcome.RecoveredFromCorruption, backupPath);
    }

    private Task WriteAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var document = new AppSettingsDocument
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            IncludeDayPeriod = settings.IncludeDayPeriod,
            SegmentGapMs = settings.SegmentGapMs,
            NormalizeLoudness = settings.NormalizeLoudness,
            UseFullPhraseOverrides = settings.UseFullPhraseOverrides,
            PlayErrorFeedback = settings.PlayErrorFeedback,
        };

        var json = JsonSerializer.Serialize(document, CoreJsonContext.Default.AppSettingsDocument);
        return AtomicFile.WriteAllTextAsync(_locations.SettingsPath, json, cancellationToken);
    }
}

internal sealed class AppSettingsDocument
{
    public int SchemaVersion { get; set; }

    public bool? IncludeDayPeriod { get; set; }

    public int? SegmentGapMs { get; set; }

    public bool? NormalizeLoudness { get; set; }

    public bool? UseFullPhraseOverrides { get; set; }

    public bool? PlayErrorFeedback { get; set; }
}
