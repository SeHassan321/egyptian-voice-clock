using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using VoiceClock.Core.Playback;

namespace VoiceClock.App.Droid;

/// <summary>
/// Home-screen widget: one large button that speaks the time immediately, without opening the app.
/// </summary>
[BroadcastReceiver(Label = "@string/widget_label", Exported = true)]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/voice_clock_widget_info")]
public sealed class VoiceClockWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null)
        {
            return;
        }

        foreach (var id in appWidgetIds)
        {
            appWidgetManager.UpdateAppWidget(id, BuildViews(context));
        }
    }

    private static RemoteViews BuildViews(Context context)
    {
        var views = new RemoteViews(context.PackageName, Resource.Layout.voice_clock_widget);
        var intent = new Intent(context, typeof(AnnounceTimeService));
        intent.SetAction(AnnounceTimeService.ActionAnnounce);

        // Tapping the widget starts the speaking service directly (allowed from a widget tap).
        var pending = PendingIntent.GetForegroundService(context, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        views.SetOnClickPendingIntent(Resource.Id.widget_root, pending);
        return views;
    }
}

/// <summary>
/// Short-lived foreground service that speaks the current time for the widget, then stops.
/// It uses exactly the same announcement pipeline as the app's main button.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class AnnounceTimeService : Service
{
    public const string ActionAnnounce = "com.egyptianvoiceclock.app.action.ANNOUNCE_TIME";
    private const string ChannelId = "voice_clock_speaking";
    private const int NotificationId = 4101;

    private readonly object _sync = new();
    private int _activeRequests;
    private int _lastStartId;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        StartAsForeground();
        lock (_sync)
        {
            _activeRequests++;
            _lastStartId = startId;
        }

        _ = AnnounceAsync();
        return StartCommandResult.NotSticky;
    }

    private async Task AnnounceAsync()
    {
        try
        {
            var announcer = IPlatformApplication.Current?.Services.GetService<IAnnouncementService>();
            if (announcer is not null)
            {
                // A second tap while speaking is ignored by the service (no overlap, no queue).
                await announcer.AnnounceNowAsync();
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("VoiceClock", "Widget announcement failed: " + ex);
        }
        finally
        {
            int startId;
            bool last;
            lock (_sync)
            {
                _activeRequests--;
                last = _activeRequests == 0;
                startId = _lastStartId;
            }

            if (last)
            {
                StopForeground(StopForegroundFlags.Remove);
                StopSelf(startId);
            }
        }
    }

    private void StartAsForeground()
    {
        if (GetSystemService(NotificationService) is NotificationManager manager && manager.GetNotificationChannel(ChannelId) is null)
        {
            var channel = new NotificationChannel(ChannelId, GetString(Resource.String.widget_channel_name), NotificationImportance.Low);
            channel.SetSound(null, null);
            channel.EnableVibration(false);
            manager.CreateNotificationChannel(channel);
        }

        var notification = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Drawable.ic_stat_voice_clock)!
            .SetContentTitle(GetString(Resource.String.widget_speaking))!
            .SetOngoing(true)!
            .Build()!;

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }
}
