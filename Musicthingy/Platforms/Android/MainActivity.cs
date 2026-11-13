using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;

namespace Musicthingy;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    // True while the activity is not visible (app in background or screen off).
    // Used by the playback progress timer to skip work that only matters when
    // the user can actually see it (slider, lyrics highlight).
    public static volatile bool IsInBackground;

    protected override void OnPause()
    {
        IsInBackground = true;
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        IsInBackground = false;
    }

    // Catches Bluetooth headset clicks and keyboard media keys globally
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        return base.DispatchKeyEvent(e);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleIntent(Intent);

        // v8.0: the built-in ON-DEVICE GVS PO token generator (a hidden
        // WebView running the youtube.com web player's BotGuard attestation
        // flow — see Platforms/Android/WebViewPoTokenProvider.cs). Registers
        // the mint hook and warms the generator ~3s after boot, so SABR-only
        // tracks stream full-length (and seek) past YouTube's ~60s
        // attestation cutoff without any external server or manual import.
        Platforms.Android.WebViewPoTokenProvider.Initialize();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    private void HandleIntent(Intent? intent)
    {
        // When clicked from the notification, ask the ViewModel to center the
        // track list on the currently playing track (manual scroll-to-track).
        if (intent?.Action == "MUSIC_ACTION_OPEN_APP")
        {
            WeakReferenceMessenger.Default.Send(new MediaControlMessage { Action = "ScrollToCurrentTrack" });
        }
    }
}