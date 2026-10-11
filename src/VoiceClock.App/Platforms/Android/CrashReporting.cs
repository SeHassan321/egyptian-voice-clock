using Android.Runtime;
using VoiceClock.Core.Diagnostics;

namespace VoiceClock.App.Droid;

/// <summary>Writes unhandled exceptions to the problem report before the process ends.</summary>
public static class CrashReporting
{
    private static int _registered;

    public static void Register(FileAppLog log)
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                log.Crash(exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Warn("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => log.Crash(e.Exception);
    }
}
