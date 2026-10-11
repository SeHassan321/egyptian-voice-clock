using VoiceClock.Core.Audio;

namespace VoiceClock.Core.Storage;

/// <summary>Where the audio for an asset currently comes from.</summary>
public enum AssetSource
{
    None,

    /// <summary>Recorded on this phone (overrides the default).</summary>
    User,

    /// <summary>The default voice shipped inside the app.</summary>
    Bundled,
}

public enum AssetState
{
    Missing,
    Ready,
    Invalid,
}

/// <summary>Current state of one recording, as shown in the checklist.</summary>
public sealed record AssetStatus(
    string Id,
    AssetState State,
    AssetSource Source,
    bool HasUserRecording,
    bool HasBundledDefault,
    TimeSpan? Duration,
    string? Problem)
{
    public bool IsReady => State == AssetState.Ready;
}

/// <summary>What was found in the voice pack bundled with the app.</summary>
public sealed record BundledVoiceInfo(bool Present, int ClipCount, string? Problem)
{
    public static BundledVoiceInfo None { get; } = new(false, 0, null);
}

/// <summary>Which recordings an export contains.</summary>
public enum VoicePackScope
{
    /// <summary>Only recordings made on this phone (a personal backup).</summary>
    UserRecordings,

    /// <summary>Every available clip, including defaults (used to build the app's default voice).</summary>
    Everything,
}

public sealed record VoicePackImportResult(bool Accepted, int Imported, int MatchedDefault, IReadOnlyList<VoicePackProblem> Problems)
{
    public static VoicePackImportResult Rejected(IReadOnlyList<VoicePackProblem> problems) => new(false, 0, 0, problems);
}

public sealed record VerificationResult(int Checked, IReadOnlyList<string> InvalidIds)
{
    public bool AllValid => InvalidIds.Count == 0;
}

public sealed class AssetsChangedEventArgs : EventArgs
{
    public AssetsChangedEventArgs(IReadOnlyCollection<string>? assetIds)
    {
        AssetIds = assetIds ?? [];
    }

    /// <summary>The ids that changed; empty means "anything may have changed".</summary>
    public IReadOnlyCollection<string> AssetIds { get; }

    public bool AllChanged => AssetIds.Count == 0;
}

/// <summary>Supplies the default voice pack shipped inside the app package.</summary>
public interface IBundledVoicePackSource
{
    /// <summary>Must change whenever the bundled pack may have changed (for example, the app build number).</summary>
    string VersionTag { get; }

    /// <summary>Opens the bundled pack, or returns null when the app ships without one.</summary>
    Task<Stream?> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>Used when no default voice is bundled.</summary>
public sealed class NoBundledVoicePack : IBundledVoicePackSource
{
    public string VersionTag => "none";

    public Task<Stream?> OpenAsync(CancellationToken cancellationToken) => Task.FromResult<Stream?>(null);
}

/// <summary>Stores, validates and serves every recording.</summary>
public interface IVoiceAssetRepository
{
    event EventHandler<AssetsChangedEventArgs>? Changed;

    bool IsInitialized { get; }

    BundledVoiceInfo BundledVoice { get; }

    /// <summary>Loads state from disk (idempotent and safe to call from several places).</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    AssetStatus GetStatus(string assetId);

    bool IsAvailable(string assetId);

    /// <summary>Complete-sentence overrides that are currently available.</summary>
    IReadOnlyList<string> AvailableFullPhraseIds { get; }

    /// <summary>The effective audio (own recording first, then the default), or null.</summary>
    Task<PcmAudio?> LoadAsync(string assetId, CancellationToken cancellationToken = default);

    Task SaveUserClipAsync(string assetId, PcmAudio audio, CancellationToken cancellationToken = default);

    /// <summary>Deletes the own recording; the default (if any) becomes effective again.</summary>
    Task<bool> DeleteUserClipAsync(string assetId, CancellationToken cancellationToken = default);

    /// <summary>Fully decodes every stored clip and marks damaged ones.</summary>
    Task<VerificationResult> VerifyAllAsync(CancellationToken cancellationToken = default);

    Task<int> ExportAsync(Stream destination, VoicePackScope scope, string? appVersion = null, CancellationToken cancellationToken = default);

    Task<VoicePackImportResult> ImportAsync(Stream source, CancellationToken cancellationToken = default);
}
