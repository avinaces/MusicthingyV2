using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using CommunityToolkit.Mvvm.Messaging;

namespace Musicthingy;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    // Catches Bluetooth headset clicks and keyboard media keys globally
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e?.Action == KeyEventActions.Down)
        {
            if (e.KeyCode == Keycode.MediaNext)
            {
                WeakReferenceMessenger.Default.Send(new Models.MediaControlMessage { Action = "Next" });
                return true; // Tell Android we handled this click
            }
            else if (e.KeyCode == Keycode.MediaPrevious)
            {
                WeakReferenceMessenger.Default.Send(new Models.MediaControlMessage { Action = "Previous" });
                return true; // Tell Android we handled this click
            }
        }

        return base.DispatchKeyEvent(e);
    }
}