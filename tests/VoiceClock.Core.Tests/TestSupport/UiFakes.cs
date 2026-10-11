using VoiceClock.Core.ViewModels;

namespace VoiceClock.Core.Tests.TestSupport;

internal sealed class FakeVolume : IVolumeControl
{
    public bool IsMuted { get; set; }

    public int Raised { get; private set; }

    public int Lowered { get; private set; }

    public int PanelShown { get; private set; }

    public void Raise() => Raised++;

    public void Lower() => Lowered++;

    public void ShowSystemVolumePanel() => PanelShown++;
}

internal sealed class FakeNavigation : INavigationService
{
    public List<string> Visits { get; } = [];

    public Exception? ThrowOnNext { get; set; }

    public Task OpenSetupAsync() => Visit("setup");

    public Task OpenRecordingsAsync() => Visit("recordings");

    public Task OpenRecorderAsync(string assetId) => Visit("recorder:" + assetId);

    public Task GoBackAsync() => Visit("back");

    private Task Visit(string target)
    {
        if (ThrowOnNext is { } ex)
        {
            ThrowOnNext = null;
            throw ex;
        }

        Visits.Add(target);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public Queue<bool> Answers { get; } = new();

    public List<string> Shown { get; } = [];

    public Task ShowMessageAsync(string title, string message)
    {
        Shown.Add(title + ": " + message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        Shown.Add(title + ": " + message);
        return Task.FromResult(Answers.Count > 0 && Answers.Dequeue());
    }
}

internal sealed class FakeMicrophone : IMicrophonePermission
{
    public MicrophoneAccess Access { get; set; } = MicrophoneAccess.Granted;

    public int Requests { get; private set; }

    public int SettingsOpened { get; private set; }

    public Task<MicrophoneAccess> RequestAsync()
    {
        Requests++;
        return Task.FromResult(Access);
    }

    public void OpenAppSettings() => SettingsOpened++;
}

internal sealed class FakeBackupFiles : IBackupFiles
{
    public bool UserCancelsSave { get; set; }

    public byte[]? PickedFile { get; set; }

    public string? LastFileName { get; private set; }

    public byte[]? LastWritten { get; private set; }

    public bool LastWasShare { get; private set; }

    public async Task<bool> SaveAsync(string suggestedFileName, Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken = default)
    {
        if (UserCancelsSave)
        {
            return false;
        }

        await Capture(suggestedFileName, writeContent, share: false, cancellationToken);
        return true;
    }

    public Task ShareAsync(string fileName, Func<Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken = default) =>
        Capture(fileName, writeContent, share: true, cancellationToken);

    public Task<Stream?> PickAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(PickedFile is null ? null : new MemoryStream(PickedFile));

    private async Task Capture(string fileName, Func<Stream, CancellationToken, Task> writeContent, bool share, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await writeContent(memory, cancellationToken);
        LastFileName = fileName;
        LastWritten = memory.ToArray();
        LastWasShare = share;
    }
}

internal sealed class FakeAppVersion : IAppVersion
{
    public string VersionLabel => "1.0.0 (1)";
}
