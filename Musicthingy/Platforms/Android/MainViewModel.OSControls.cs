using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Platforms.Android;
using Musicthingy.Models;
using Microsoft.Maui.ApplicationModel;
using Stream = Android.Media.Stream;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private MediaSession? _mediaSession;
    private Android.Graphics.Bitmap? _currentAndroidArt;
    private VolumeChangeReceiver? _volumeReceiver;
    private PowerManager.WakeLock? _wakeLock;
    private Android.Net.Wifi.WifiManager.WifiLock? _wifiLock;

    partial void CleanupOSMediaPlatform()
    {
        try
        {
            Context? context = Android.App.Application.Context;
            if (context != null)
            {
                Intent intent = new Intent(context, typeof(AndroidMediaService));
                context.UnregisterReceiver(_volumeReceiver);
                intent.SetAction("STOP");
                context.StartService(intent);
            }
        }
        catch { }
    }

    private partial void SyncOSPlaybackStatePlatform()
    {
        if (_mediaSession == null) SetupAndroidWidgets();

        if (_mediaSession != null)
        {
            long actions = PlaybackState.ActionPlay | PlaybackState.ActionPause |
                           PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious |
                           PlaybackState.ActionSeekTo;
            PlaybackStateCode stateCode = IsPlaying ? PlaybackStateCode.Playing : PlaybackStateCode.Paused;

            PlaybackState state = new PlaybackState.Builder()
                .SetActions(actions)
                .SetState(stateCode, (long)CurrentPosition.TotalMilliseconds, 1.0f)
                .Build();

            _mediaSession.SetPlaybackState(state);
            UpdateAndroidNotification();
        }
    }

    private async partial Task UpdateOSMediaWidgetsPlatformAsync(string title, string artist, string iconUrl)
    {
        SetupAndroidWidgets();
        if (_mediaSession == null) return;

        _currentAndroidArt = await LoadAndroidBitmapAsync(title, iconUrl);

        MediaMetadata.Builder metaBuilder = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, title)
            .PutString(MediaMetadata.MetadataKeyArtist, artist)
            .PutLong(MediaMetadata.MetadataKeyDuration, (long)Duration.TotalMilliseconds);

        if (_currentAndroidArt != null)
        {
            metaBuilder.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, _currentAndroidArt);
        }

        _mediaSession.SetMetadata(metaBuilder.Build());
        SyncOSPlaybackStatePlatform();
    }

    private void SetupAndroidWidgets()
    {
        if (_mediaSession != null) return;

        Context? context = Android.App.Application.Context;
        if (context == null) return;

        _mediaSession = new MediaSession(context, "MusicthingyMediaSession");
        _mediaSession.SetCallback(new CustomMediaSessionCallback(this));
        _mediaSession.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
        _mediaSession.Active = true;

        _volumeReceiver = new VolumeChangeReceiver(this);
        IntentFilter filter = new IntentFilter("android.media.VOLUME_CHANGED_ACTION");
        context.RegisterReceiver(_volumeReceiver, filter);
    }

    private void UpdateAndroidNotification()
    {
        Context? context = Android.App.Application.Context;
        if (context == null || _mediaSession == null) return;

        NotificationManager? nm = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (nm == null) return;

#pragma warning disable CA1416 
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            NotificationChannel channel = new NotificationChannel("musicthingy_media", "Media Playback", NotificationImportance.Low);
            nm.CreateNotificationChannel(channel);
        }

        Android.App.Notification.MediaStyle mediaStyle = new Android.App.Notification.MediaStyle();
        mediaStyle.SetMediaSession(_mediaSession.SessionToken);
        mediaStyle.SetShowActionsInCompactView(0, 1, 2);


        PendingIntent? pendingOpenAppIntent = null;
        Intent? openAppIntent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (openAppIntent != null)
        {
            openAppIntent.SetAction("MUSIC_ACTION_OPEN_APP");
            openAppIntent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            pendingOpenAppIntent = PendingIntent.GetActivity(context, 0, openAppIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        }

        Android.App.Notification.Builder builder = new Android.App.Notification.Builder(context, "musicthingy_media")
            .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)
            .SetContentTitle(CurrentTrack?.Title ?? "Unknown Track")
            .SetContentText(CurrentTrack?.Playlist?.Name ?? "Musicthingy")
            .SetStyle(mediaStyle)
            .SetOngoing(IsPlaying)
            .SetVisibility(NotificationVisibility.Public);

        if (pendingOpenAppIntent != null)
        {
            builder.SetContentIntent(pendingOpenAppIntent);
        }

        Android.Graphics.Drawables.Icon iconPrev = Android.Graphics.Drawables.Icon.CreateWithResource(context, Android.Resource.Drawable.IcMediaPrevious);
        Android.App.Notification.Action actionPrev = new Android.App.Notification.Action.Builder(iconPrev, "Previous", CreateActionIntent("MUSIC_ACTION_PREV", 1)).Build();
        builder.AddAction(actionPrev);

        int playPauseRes = IsPlaying ? Android.Resource.Drawable.IcMediaPause : Android.Resource.Drawable.IcMediaPlay;
        Android.Graphics.Drawables.Icon iconPlayPause = Android.Graphics.Drawables.Icon.CreateWithResource(context, playPauseRes);
        Android.App.Notification.Action actionPlayPause = new Android.App.Notification.Action.Builder(iconPlayPause, IsPlaying ? "Pause" : "Play", CreateActionIntent("MUSIC_ACTION_PLAY_PAUSE", 2)).Build();
        builder.AddAction(actionPlayPause);

        Android.Graphics.Drawables.Icon iconNext = Android.Graphics.Drawables.Icon.CreateWithResource(context, Android.Resource.Drawable.IcMediaNext);
        Android.App.Notification.Action actionNext = new Android.App.Notification.Action.Builder(iconNext, "Next", CreateActionIntent("MUSIC_ACTION_NEXT", 3)).Build();
        builder.AddAction(actionNext);

        Android.App.Notification notification = builder.Build();

        AndroidMediaService.CurrentNotification = notification;

        if (!AndroidMediaService.IsRunning)
        {
            Intent serviceIntent = new Intent(context, typeof(AndroidMediaService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(serviceIntent);
            else
                context.StartService(serviceIntent);
        }

        nm.Notify(1, notification);
#pragma warning restore CA1416
    }

    private async Task<Android.Graphics.Bitmap?> LoadAndroidBitmapAsync(string title, string iconUrl)
    {
        Android.Graphics.Bitmap? bitmap = null;
        try
        {
            if (string.IsNullOrEmpty(iconUrl))
            {
                int size = 1024;
                bitmap = Android.Graphics.Bitmap.CreateBitmap(size, size, Android.Graphics.Bitmap.Config.Argb8888!);
                using Android.Graphics.Canvas canvas = new Android.Graphics.Canvas(bitmap);

                int hash = (title ?? "").GetHashCode();
                int r = Math.Max(Math.Abs((hash & 0xFF0000) >> 16), 50);
                int g = Math.Max(Math.Abs((hash & 0x00FF00) >> 8), 50);
                int b = Math.Max(Math.Abs(hash & 0x0000FF), 50);

                canvas.DrawColor(Android.Graphics.Color.Rgb(r, g, b));

                Android.Graphics.Paint paint = new Android.Graphics.Paint
                {
                    Color = Android.Graphics.Color.White,
                    TextSize = size / 2f,
                    TextAlign = Android.Graphics.Paint.Align.Center,
                    AntiAlias = true
                };

                string text = string.IsNullOrEmpty(title) ? "?" : title.Substring(0, 1).ToUpper();
                Android.Graphics.Paint.FontMetrics? fontMetrics = paint.GetFontMetrics();
                float y = (size / 2f) - ((fontMetrics!.Descent + fontMetrics.Ascent) / 2f);
                canvas.DrawText(text, size / 2f, y, paint);
            }
            else if (iconUrl.StartsWith("data:image"))
            {
                string base64Data = iconUrl.Substring(iconUrl.IndexOf(',') + 1);
                byte[] bytes = Convert.FromBase64String(base64Data);
                bitmap = await BitmapFactory.DecodeByteArrayAsync(bytes, 0, bytes.Length);
            }
            else if (iconUrl.StartsWith("http"))
            {
                using HttpClient client = new HttpClient();
                byte[] bytes = await client.GetByteArrayAsync(iconUrl);
                bitmap = await BitmapFactory.DecodeByteArrayAsync(bytes, 0, bytes.Length);
            }
            else if (File.Exists(iconUrl))
            {
                bitmap = await BitmapFactory.DecodeFileAsync(iconUrl);
            }

            if (bitmap != null)
            {
                int targetSize = 1024;
                if (bitmap.Width < targetSize || bitmap.Height < targetSize)
                {
                    float ratio = Math.Max((float)targetSize / bitmap.Width, (float)targetSize / bitmap.Height);
                    int newWidth = (int)(bitmap.Width * ratio);
                    int newHeight = (int)(bitmap.Height * ratio);

                    Android.Graphics.Bitmap? scaled = Android.Graphics.Bitmap.CreateScaledBitmap(bitmap, newWidth, newHeight, filter: true);
                    if (scaled != null && scaled != bitmap)
                    {
                        bitmap.Recycle();
                        bitmap = scaled;
                    }
                }
            }
        }
        catch { }
        return bitmap;
    }

    private PendingIntent CreateActionIntent(string action, int requestCode)
    {
        Context? context = Android.App.Application.Context;
        Intent intent = new Intent(context, typeof(MediaActionReceiver));
        intent.SetAction(action);
        return PendingIntent.GetBroadcast(context, requestCode, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    private partial void AcquireTransientWakeLockPlatform()
    {
        try
        {
            Context? context = Android.App.Application.Context;

            if (_wakeLock == null)
            {
                PowerManager? pm = (PowerManager?)context.GetSystemService(Context.PowerService);
                _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "Musicthingy::Wakelock");
                _wakeLock?.SetReferenceCounted(false);
            }
            _wakeLock?.Acquire();

            if (_wifiLock == null)
            {
                Android.Net.Wifi.WifiManager? wm = (Android.Net.Wifi.WifiManager?)context.GetSystemService(Context.WifiService);
                _wifiLock = wm?.CreateWifiLock(Android.Net.WifiMode.FullHighPerf, "Musicthingy::WifiLock");
                _wifiLock?.SetReferenceCounted(false);
            }
            _wifiLock?.Acquire();
        }
        catch { }
    }

    private partial void ReleaseTransientWakeLockPlatform()
    {
        try
        {
            if (_wakeLock?.IsHeld == true) _wakeLock.Release();
            if (_wifiLock?.IsHeld == true) _wifiLock.Release();
        }
        catch { }
    }

    private class CustomMediaSessionCallback : MediaSession.Callback
    {
        private readonly MainViewModel _vm;
        public CustomMediaSessionCallback(MainViewModel vm) { _vm = vm; }

        public override void OnPlay() => MainThread.BeginInvokeOnMainThread(() => _vm.TogglePlayCommand.Execute(null));
        public override void OnPause() => MainThread.BeginInvokeOnMainThread(() => _vm.TogglePlayCommand.Execute(null));
        public override void OnSkipToNext() => MainThread.BeginInvokeOnMainThread(() => _vm.PlayNextCommand.Execute(null));
        public override void OnSkipToPrevious() => MainThread.BeginInvokeOnMainThread(() => _vm.PlayPreviousCommand.Execute(null));

        public override void OnSeekTo(long pos)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _vm.CurrentPositionSeconds = pos / 1000.0;
                _vm.CurrentPosition = TimeSpan.FromMilliseconds(pos);
                _vm.SeekCommand.Execute(null);
                _vm.SyncOSPlaybackState();
            });
        }
    }

    private class VolumeChangeReceiver : BroadcastReceiver
    {
        private readonly MainViewModel _vm;
        public VolumeChangeReceiver(MainViewModel vm) { _vm = vm; }

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == "android.media.VOLUME_CHANGED_ACTION")
            {
                int streamType = intent.GetIntExtra("android.media.EXTRA_VOLUME_STREAM_TYPE", -1);

                if (streamType == (int)Stream.Music)
                {
                    int newVolume = intent.GetIntExtra("android.media.EXTRA_VOLUME_STREAM_VALUE", -1);
                    if (newVolume != -1)
                    {
                        MainThread.BeginInvokeOnMainThread(() => _vm.HandleVolumeChange(newVolume));
                    }
                }
            }
        }
    }
}