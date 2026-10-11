using Android.App;
using Android.Content;
using Android.Content.PM;
using VoiceClock.App.Droid;

namespace VoiceClock.App;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>Routes document-picker results (backup save/restore) back to the awaiting code.</summary>
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (!ActivityResults.TryComplete(requestCode, resultCode, data))
        {
            base.OnActivityResult(requestCode, resultCode, data);
        }
    }
}
