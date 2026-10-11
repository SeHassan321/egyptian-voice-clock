namespace VoiceClock.Core.Storage;

/// <summary>
/// Where everything lives, relative to the app's private data directory (on Android,
/// <c>FileSystem.AppDataDirectory</c>). No external or hard-coded paths are used.
/// </summary>
public sealed class StorageLocations
{
    public StorageLocations(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }

    public string VoiceDirectory => Path.Combine(DataDirectory, "voice");

    /// <summary>The owner's own recordings, one WAV per asset id.</summary>
    public string UserClipsDirectory => Path.Combine(VoiceDirectory, "user");

    /// <summary>Default voice extracted from the voice pack bundled in the APK.</summary>
    public string BundledClipsDirectory => Path.Combine(VoiceDirectory, "bundled");

    public string ManifestPath => Path.Combine(VoiceDirectory, "manifest.json");

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string ExportDirectory => Path.Combine(DataDirectory, "exports");
}
