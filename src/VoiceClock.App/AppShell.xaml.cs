using VoiceClock.App.Pages;

namespace VoiceClock.App;

public static class Routes
{
    public const string Setup = "setup";
    public const string Recordings = "recordings";
    public const string Recorder = "recorder";
}

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Pages are resolved from the DI container, so their view models are injected.
        Routing.RegisterRoute(Routes.Setup, typeof(SetupPage));
        Routing.RegisterRoute(Routes.Recordings, typeof(RecordingsPage));
        Routing.RegisterRoute(Routes.Recorder, typeof(RecordClipPage));
    }
}
