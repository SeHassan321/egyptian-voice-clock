using Microsoft.Extensions.DependencyInjection;
using VoiceClock.Core.Playback;
using VoiceClock.Core.Recording;

namespace VoiceClock.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(_services.GetRequiredService<AppShell>());

    /// <summary>Leaving the app stops speech and releases the microphone.</summary>
    protected override void OnSleep()
    {
        base.OnSleep();
        _services.GetService<IAnnouncementService>()?.Stop();
        if (_services.GetService<IRecordingService>() is { } recorder)
        {
            recorder.StopPreview();
            _ = recorder.CancelAsync();
        }
    }
}
