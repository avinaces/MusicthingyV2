using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using Microsoft.Maui.ApplicationModel;

namespace Musicthingy.Platforms.Android;

[Service(ForegroundServiceType = ForegroundService.TypeMediaPlayback, Exported = false)]
public class AndroidMediaService : Service
{
    public static bool IsRunning { get; private set; }
    public static global::Android.App.Notification? CurrentNotification { get; set; }

    // True while audio is ACTUALLY playing (synced by MainViewModel's
    // UpdateAndroidNotification on every state change; cleared on teardown).
    public static volatile bool IsMediaPlaying;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        IsRunning = true;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "STOP")
        {
            IsRunning = false;
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        try
        {
            if (CurrentNotification == null)
            {
                NotificationManager? nm = (NotificationManager?)GetSystemService(Context.NotificationService);
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    NotificationChannel channel = new NotificationChannel("musicthingy_media", "Media Playback", NotificationImportance.Low);
                    nm?.CreateNotificationChannel(channel);
                }

                // CREATE THE LAUNCH INTENT:
                PendingIntent? pendingOpenAppIntent = null;
                Intent? openAppIntent = PackageManager?.GetLaunchIntentForPackage(PackageName!);
                if (openAppIntent != null)
                {
                    openAppIntent.SetAction("MUSIC_ACTION_OPEN_APP");
                    openAppIntent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
                    pendingOpenAppIntent = PendingIntent.GetActivity(this, 0, openAppIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                }

                var builder = new global::Android.App.Notification.Builder(this, "musicthingy_media")
                    .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
                    .SetContentTitle("Musicthingy");

                if (pendingOpenAppIntent != null)
                    builder.SetContentIntent(pendingOpenAppIntent);

                CurrentNotification = builder.Build();
            }

#pragma warning disable CA1416 
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(1, CurrentNotification, ForegroundService.TypeMediaPlayback);
            else
                StartForeground(1, CurrentNotification);
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AndroidMediaService] Failed to StartForeground: {ex}");
        }

        // NotSticky: if the system kills the process, a sticky restart would
        // resurrect an EMPTY foreground service (statics are gone) showing a
        // bare 'Musicthingy' notification and burning battery for nothing.
        // Normal operation is unaffected - the service stays alive as long
        // as the process does; the ViewModel stops it on teardown.
        return StartCommandResult.NotSticky;
    }

    // Called when the user swipes the app out of recents. While media is
    // actually playing the service must survive so audio keeps going in
    // the background. When nothing is playing there is no reason to keep
    // a foreground process: stop it so the app does not linger as an
    // active background app after the swipe.
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        if (!IsMediaPlaying)
        {
            try
            {
                ((NotificationManager?)GetSystemService(Context.NotificationService))?.Cancel(1);
                StopForeground(StopForegroundFlags.Remove);
            }
            catch { }
            StopSelf();
        }
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        base.OnDestroy();
    }
}

[BroadcastReceiver(Exported = true)]
[IntentFilter(new[] { "MUSIC_ACTION_PLAY_PAUSE", "MUSIC_ACTION_NEXT", "MUSIC_ACTION_PREV", "MUSIC_ACTION_NOTIFICATION_DELETED" })]
public class MediaActionReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (intent?.Action == "MUSIC_ACTION_PLAY_PAUSE")
                WeakReferenceMessenger.Default.Send(new MediaControlMessage { Action = "PlayPause" });
            else if (intent?.Action == "MUSIC_ACTION_NEXT")
                WeakReferenceMessenger.Default.Send(new MediaControlMessage { Action = "Next" });
            else if (intent?.Action == "MUSIC_ACTION_PREV")
                WeakReferenceMessenger.Default.Send(new MediaControlMessage { Action = "Previous" });
            else if (intent?.Action == "MUSIC_ACTION_NOTIFICATION_DELETED")
            {
                // While playing the notification is 'ongoing' and cannot be
                // swiped away, so this only fires when nothing is audible.
                // Stop the foreground service so no background process
                // outlives a dismissed media widget.
                if (!AndroidMediaService.IsMediaPlaying && context != null)
                {
                    try { context.StopService(new Intent(context, typeof(AndroidMediaService))); } catch { }
                }
            }
        });
    }
}