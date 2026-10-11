using Microsoft.Extensions.DependencyInjection;
using VoiceClock.App.Droid;
using VoiceClock.App.Pages;
using VoiceClock.App.Services;
using VoiceClock.Core.Catalog;
using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.Playback;
using VoiceClock.Core.Recording;
using VoiceClock.Core.Settings;
using VoiceClock.Core.Startup;
using VoiceClock.Core.Storage;
using VoiceClock.Core.Time;
using VoiceClock.Core.ViewModels;

namespace VoiceClock.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        var services = builder.Services;

        // Diagnostics: warnings and crashes go to logcat and to a small private file the owner can send.
        services.AddSingleton(_ => new FileAppLog(
            Path.Combine(FileSystem.Current.AppDataDirectory, "logs", "app.log"),
            new AndroidAppLog()));
        services.AddSingleton<IAppLog>(sp => sp.GetRequiredService<FileAppLog>());
        services.AddSingleton<IDiagnosticLog>(sp => sp.GetRequiredService<FileAppLog>());

        // Core: rules, storage and audio logic (platform independent).
        services.AddSingleton(_ => SpeechRules.Default);
        services.AddSingleton<ITimeSpeechEngine, TimeSpeechEngine>();
        services.AddSingleton<RecordingCatalog>();
        services.AddSingleton(_ => new StorageLocations(FileSystem.Current.AppDataDirectory));
        services.AddSingleton<IBundledVoicePackSource, MauiBundledVoicePackSource>();
        services.AddSingleton<IVoiceAssetRepository, VoiceAssetRepository>();
        services.AddSingleton<ISettingsRepository, JsonSettingsRepository>();
        services.AddSingleton<AppInitializer>();
        services.AddSingleton<AudioSessionGate>();
        services.AddSingleton<IAnnouncementService, AnnouncementService>();
        services.AddSingleton<IRecordingService, RecordingService>();

        // Android implementations.
        services.AddSingleton<ILocalClock, AndroidLocalClock>();
        services.AddSingleton<IAudioOutput, AndroidAudioOutput>();
        services.AddSingleton<IAudioInput, AndroidAudioInput>();
        services.AddSingleton<IFeedbackService, AndroidFeedbackService>();
        services.AddSingleton<IVolumeControl, AndroidVolumeControl>();
        services.AddSingleton<IBackupFiles, AndroidBackupFiles>();

        // UI services.
        services.AddSingleton<IUiDispatcher, MauiUiDispatcher>();
        services.AddSingleton<IDialogService, MauiDialogService>();
        services.AddSingleton<INavigationService, ShellNavigationService>();
        services.AddSingleton<IMicrophonePermission, MicrophonePermissionService>();
        services.AddSingleton<IAppVersion, MauiAppVersion>();

        // Pages and view models.
        services.AddSingleton<AppShell>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainPage>();
        services.AddTransient<SetupViewModel>();
        services.AddTransient<SetupPage>();
        services.AddTransient<RecordingsViewModel>();
        services.AddTransient<RecordingsPage>();
        services.AddTransient<RecordClipViewModel>();
        services.AddTransient<RecordClipPage>();

        var app = builder.Build();
        CrashReporting.Register(app.Services.GetRequiredService<FileAppLog>());
        return app;
    }
}
