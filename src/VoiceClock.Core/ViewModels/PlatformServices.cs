namespace VoiceClock.Core.ViewModels;

/// <summary>Runs an action on the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Runs immediately on the calling thread (tests and non-UI hosts).</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
}

public interface INavigationService
{
    Task OpenSetupAsync();

    Task OpenRecordingsAsync();

    Task OpenRecorderAsync(string assetId);

    Task GoBackAsync();
}

public enum MicrophoneAccess
{
    Granted,
    Denied,

    /// <summary>The user chose "don't ask again"; only the system settings can grant it now.</summary>
    PermanentlyDenied,
}

/// <summary>Microphone permission, requested only when the user starts a recording.</summary>
public interface IMicrophonePermission
{
    Task<MicrophoneAccess> RequestAsync();

    void OpenAppSettings();
}

/// <summary>Android media volume.</summary>
public interface IVolumeControl
{
    bool IsMuted { get; }

    void Raise();

    void Lower();

    void ShowSystemVolumePanel();
}

/// <summary>User-chosen locations for backups, through the system document picker and share sheet.</summary>
public interface IBackupFiles
{
    /// <summary>Asks where to save, then writes the content. Returns false if the user cancelled.</summary>
    Task<bool> SaveAsync(string suggestedFileName, Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken = default);

    /// <summary>Writes the content to a temporary file and opens the share sheet (WhatsApp, Drive, e-mail…).</summary>
    Task ShareAsync(string fileName, Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken = default);

    /// <summary>Lets the user choose a backup to restore; null if cancelled.</summary>
    Task<Stream?> PickAsync(CancellationToken cancellationToken = default);
}

public interface IAppInfo
{
    /// <summary>For example "1.0.0 (12)".</summary>
    string VersionLabel { get; }
}
