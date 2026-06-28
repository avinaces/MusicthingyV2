using Android.App;
using Android.Content;
using Android.Media;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using Stream = Android.Media.Stream;

namespace Musicthingy.Platforms.Android;

[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter(new[] { "android.media.VOLUME_CHANGED_ACTION" })]
public class VolumeChangeReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action == "android.media.VOLUME_CHANGED_ACTION" && context != null)
        {
            // Get the current media stream volume
            AudioManager? audioManager = (AudioManager?)context.GetSystemService(Context.AudioService);

            if (audioManager != null)
            {
                int currentVolume = audioManager.GetStreamVolume(Stream.Music);

                // Send it to the ViewModel
                WeakReferenceMessenger.Default.Send(new HardwareVolumeMessage(currentVolume));
            }
        }
    }
}