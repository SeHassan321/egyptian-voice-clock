using VoiceClock.Core.Playback;
using VoiceClock.Core.Recording;
using VoiceClock.Core.Settings;
using VoiceClock.Core.Startup;
using VoiceClock.Core.Storage;

namespace VoiceClock.Core.Tests.TestSupport;

/// <summary>The real core services wired together over a temporary folder and fake platform audio.</summary>
internal sealed class TestRig : IDisposable
{
    private readonly TempDirectory _temp = new();

    private readonly IBundledVoicePackSource _bundled;

    public TestRig(IBundledVoicePackSource? bundled = null)
    {
        Locations = new StorageLocations(_temp.Path);
        _bundled = bundled ?? new NoBundledVoicePack();
        Restart();
    }

    public StorageLocations Locations { get; }

    public VoiceAssetRepository Repository { get; private set; } = null!;

    public JsonSettingsRepository Settings { get; private set; } = null!;

    public AppInitializer Initializer { get; private set; } = null!;

    public AudioSessionGate Gate { get; } = new();

    public FakeAudioOutput Output { get; } = new();

    public FakeAudioInput Input { get; } = new();

    public FakeFeedback Feedback { get; } = new();

    public FakeClock Clock { get; } = new();

    public AnnouncementService Announcer { get; private set; } = null!;

    public RecordingService Recorder { get; private set; } = null!;

    /// <summary>Recreates every service over the same folder, like closing and reopening the app.</summary>
    public void Restart()
    {
        Repository = new VoiceAssetRepository(Locations, TestCatalog.Catalog, _bundled);
        Settings = new JsonSettingsRepository(Locations);
        Initializer = new AppInitializer(Settings, Repository);
        Announcer = new AnnouncementService(TestCatalog.Engine, Repository, Settings, Output, Feedback, Gate, Clock, Initializer);
        Recorder = new RecordingService(Input, Output, Repository, TestCatalog.Catalog, Gate);
    }

    public async Task RecordAsync(params string[] ids)
    {
        foreach (var id in ids)
        {
            await Repository.SaveUserClipAsync(id, PackBuilder.ClipFor(id));
        }
    }

    public Task RecordAllRequiredAsync(bool includeDayPeriod = false, params string[] except) =>
        RecordAsync(TestCatalog.Catalog.RequiredEntries(includeDayPeriod).Select(e => e.Id).Except(except).ToArray());

    public async Task UpdateSettingsAsync(Func<AppSettings, AppSettings> change)
    {
        await Initializer.EnsureInitializedAsync();
        await Settings.SaveAsync(change(Settings.Current));
    }

    public void Dispose() => _temp.Dispose();
}
