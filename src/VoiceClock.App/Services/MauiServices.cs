using VoiceClock.Core.Resources;
using VoiceClock.Core.Storage;
using VoiceClock.Core.ViewModels;

namespace VoiceClock.App.Services;

public sealed class MauiUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (MainThread.IsMainThread)
        {
            action();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(action);
        }
    }
}

public sealed class MauiDialogService : IDialogService
{
    private static Page? CurrentPage => Shell.Current?.CurrentPage ?? Application.Current?.Windows.FirstOrDefault()?.Page;

    public Task ShowMessageAsync(string title, string message) =>
        CurrentPage?.DisplayAlertAsync(title, message, Strings.Common_Ok) ?? Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) =>
        CurrentPage?.DisplayAlertAsync(title, message, accept, cancel) ?? Task.FromResult(false);
}

public sealed class ShellNavigationService : INavigationService
{
    public Task OpenSetupAsync() => Shell.Current.GoToAsync(Routes.Setup);

    public Task OpenRecordingsAsync() => Shell.Current.GoToAsync(Routes.Recordings);

    public Task OpenRecorderAsync(string assetId) =>
        Shell.Current.GoToAsync(Routes.Recorder, new Dictionary<string, object> { ["id"] = assetId });

    public Task GoBackAsync() => Shell.Current.GoToAsync("..");
}

/// <summary>
/// Microphone permission, asked only when the user presses "record". After a refusal with
/// "don't ask again", Android stops showing the dialog; we then offer the app's settings page.
/// </summary>
public sealed class MicrophonePermissionService : IMicrophonePermission
{
    private const string AskedBeforeKey = "microphone_permission_requested";

    public async Task<MicrophoneAccess> RequestAsync()
    {
        if (await Permissions.CheckStatusAsync<Permissions.Microphone>() == PermissionStatus.Granted)
        {
            return MicrophoneAccess.Granted;
        }

        var askedBefore = Preferences.Default.Get(AskedBeforeKey, false);
        var rationaleBefore = Permissions.ShouldShowRationale<Permissions.Microphone>();
        var status = await Permissions.RequestAsync<Permissions.Microphone>();
        Preferences.Default.Set(AskedBeforeKey, true);

        if (status == PermissionStatus.Granted)
        {
            return MicrophoneAccess.Granted;
        }

        // No rationale before or after a repeated request means the system dialog was suppressed.
        var rationaleAfter = Permissions.ShouldShowRationale<Permissions.Microphone>();
        return askedBefore && !rationaleBefore && !rationaleAfter
            ? MicrophoneAccess.PermanentlyDenied
            : MicrophoneAccess.Denied;
    }

    public void OpenAppSettings() => AppInfo.Current.ShowSettingsUI();
}

public sealed class MauiAppVersion : IAppVersion
{
    public string VersionLabel => $"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
}

/// <summary>Opens Resources/Raw/voicepack.zip from the APK, if the build includes one.</summary>
public sealed class MauiBundledVoicePackSource : IBundledVoicePackSource
{
    public string VersionTag => $"{AppInfo.Current.VersionString}+{AppInfo.Current.BuildString}";

    public async Task<Stream?> OpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await FileSystem.Current.AppPackageFileExistsAsync(VoicePackFormat.BundledFileName))
            {
                return null;
            }

            return await FileSystem.Current.OpenAppPackageFileAsync(VoicePackFormat.BundledFileName);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
