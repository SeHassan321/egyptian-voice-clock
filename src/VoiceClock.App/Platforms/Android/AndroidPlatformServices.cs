using Android.Content;
using Android.Media;
using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.Playback;
using VoiceClock.Core.ViewModels;
using AStream = Android.Media.Stream;

namespace VoiceClock.App.Droid;

public sealed class AndroidAppLog : IAppLog
{
    private const string Tag = "VoiceClock";

    public void Info(string message) => Android.Util.Log.Info(Tag, message);

    public void Warn(string message, Exception? exception = null) =>
        Android.Util.Log.Warn(Tag, exception is null ? message : $"{message} {exception}");
}

/// <summary>The hour and minute exactly as the phone's own clock shows them (device time zone).</summary>
public sealed class AndroidLocalClock : ILocalClock
{
    public TimeOnly Now()
    {
        var calendar = Java.Util.Calendar.Instance;
        return new TimeOnly(calendar.Get(Java.Util.CalendarField.HourOfDay), calendar.Get(Java.Util.CalendarField.Minute));
    }
}

/// <summary>Media volume, adjusted through the system so its own volume panel is shown.</summary>
public sealed class AndroidVolumeControl : IVolumeControl
{
    private static AudioManager? Manager =>
        Android.App.Application.Context.GetSystemService(Context.AudioService) as AudioManager;

    public bool IsMuted => Manager is { } manager && manager.GetStreamVolume(AStream.Music) == 0;

    public void Raise() => Manager?.AdjustStreamVolume(AStream.Music, Adjust.Raise, VolumeNotificationFlags.ShowUi);

    public void Lower() => Manager?.AdjustStreamVolume(AStream.Music, Adjust.Lower, VolumeNotificationFlags.ShowUi);

    public void ShowSystemVolumePanel() => Manager?.AdjustStreamVolume(AStream.Music, Adjust.Same, VolumeNotificationFlags.ShowUi);
}

/// <summary>Haptic tick for presses; vibration plus a short "negative" tone for errors.</summary>
public sealed class AndroidFeedbackService : IFeedbackService
{
    private readonly IAppLog _log;

    public AndroidFeedbackService(IAppLog log)
    {
        _log = log;
    }

    public void Tap()
    {
        try
        {
            HapticFeedback.Default.Perform(HapticFeedbackType.Click);
        }
        catch (Exception ex)
        {
            // Not available without a visible activity (e.g. from the widget); harmless.
            _log.Info("Haptic feedback unavailable: " + ex.Message);
        }
    }

    public async Task SignalErrorAsync(CancellationToken cancellationToken)
    {
        try
        {
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(450));
        }
        catch (Exception ex)
        {
            _log.Info("Vibration unavailable: " + ex.Message);
        }

        ToneGenerator? tone = null;
        try
        {
            tone = new ToneGenerator(AStream.Music, 90);
            tone.StartTone(Tone.PropNack, 500);
            await Task.Delay(550, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Warn("Error tone failed.", ex);
        }
        finally
        {
            tone?.Release();
        }
    }
}
