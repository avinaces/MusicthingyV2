#if WINDOWS
using Windows.Media;
using Windows.Storage.Streams;
#elif ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using CommunityToolkit.Mvvm.Messaging;

#endif
using System.Net.Http;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
#if WINDOWS
    private Windows.Media.Playback.MediaPlayer? _smtcPlayer;

    private void SetupWindowsWidgets()
    {
        if (_smtcPlayer != null) return;

        _smtcPlayer = new Windows.Media.Playback.MediaPlayer();
        var smtc = _smtcPlayer.SystemMediaTransportControls;
        if (smtc == null) return;

        smtc.IsEnabled = true;
        smtc.IsPlayEnabled = true;
        smtc.IsPauseEnabled = true;
        smtc.IsNextEnabled = true;
        smtc.IsPreviousEnabled = true;

        smtc.ButtonPressed += (sender, args) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (args.Button == SystemMediaTransportControlsButton.Play || args.Button == SystemMediaTransportControlsButton.Pause)
                    TogglePlayCommand.Execute(null);
                else if (args.Button == SystemMediaTransportControlsButton.Next)
                    PlayNextCommand.Execute(null);
                else if (args.Button == SystemMediaTransportControlsButton.Previous)
                    PlayPreviousCommand.Execute(null);
            });
        };
    }

    private async Task<RandomAccessStreamReference?> LoadWindowsImageAsync(string iconUrl)
    {
        if (string.IsNullOrEmpty(iconUrl)) return null;
        try
        {
            if (iconUrl.StartsWith("data:image"))
            {
                var base64Data = iconUrl.Substring(iconUrl.IndexOf(',') + 1);
                byte[] bytes = Convert.FromBase64String(base64Data);
                string tempPath = Path.Combine(FileSystem.CacheDirectory, "temp_win_art.png");
                await File.WriteAllBytesAsync(tempPath, bytes);
                return RandomAccessStreamReference.CreateFromUri(new Uri("file:///" + tempPath.Replace("\\", "/")));
            }
            else if (iconUrl.StartsWith("http"))
            {
                return RandomAccessStreamReference.CreateFromUri(new Uri(iconUrl));
            }
            else if (File.Exists(iconUrl))
            {
                return RandomAccessStreamReference.CreateFromUri(new Uri("file:///" + iconUrl.Replace("\\", "/")));
            }
        }
        catch { }
        return null;
    }
#endif

#if ANDROID
    private MediaSession? _mediaSession;
    private Android.Graphics.Bitmap? _currentAndroidArt;

    private void SetupAndroidWidgets()
    {
        if (_mediaSession != null) return;

        var context = Android.App.Application.Context;
        if (context == null) return;

        _mediaSession = new MediaSession(context, "MusicthingyMediaSession");
        _mediaSession.SetCallback(new CustomMediaSessionCallback(this));
        _mediaSession.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
        _mediaSession.Active = true;
    }

    private class CustomMediaSessionCallback : MediaSession.Callback
    {
        private readonly MainViewModel _vm;
        public CustomMediaSessionCallback(MainViewModel vm) { _vm = vm; }

        // Must run on MainThread so MAUI doesn't silently swallow the UI updates!
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

    private async Task<Android.Graphics.Bitmap?> LoadAndroidBitmapAsync(string title, string iconUrl)
    {
        Android.Graphics.Bitmap? bitmap = null;
        try
        {
            if (string.IsNullOrEmpty(iconUrl))
            {
                int size = 1024;
                bitmap = Android.Graphics.Bitmap.CreateBitmap(size, size, Android.Graphics.Bitmap.Config.Argb8888!);
                using var canvas = new Android.Graphics.Canvas(bitmap);

                int hash = (title ?? "").GetHashCode();
                int r = Math.Max(Math.Abs((hash & 0xFF0000) >> 16), 50);
                int g = Math.Max(Math.Abs((hash & 0x00FF00) >> 8), 50);
                int b = Math.Max(Math.Abs(hash & 0x0000FF), 50);

                canvas.DrawColor(Android.Graphics.Color.Rgb(r, g, b));

                var paint = new Android.Graphics.Paint
                {
                    Color = Android.Graphics.Color.White,
                    TextSize = size / 2f,
                    TextAlign = Android.Graphics.Paint.Align.Center,
                    AntiAlias = true
                };

                string text = string.IsNullOrEmpty(title) ? "?" : title.Substring(0, 1).ToUpper();
                var fontMetrics = paint.GetFontMetrics();
                float y = (size / 2f) - ((fontMetrics!.Descent + fontMetrics.Ascent) / 2f);
                canvas.DrawText(text, size / 2f, y, paint);
            }
            else if (iconUrl.StartsWith("data:image"))
            {
                var base64Data = iconUrl.Substring(iconUrl.IndexOf(',') + 1);
                byte[] bytes = Convert.FromBase64String(base64Data);
                bitmap = await BitmapFactory.DecodeByteArrayAsync(bytes, 0, bytes.Length);
            }
            else if (iconUrl.StartsWith("http"))
            {
                using var client = new HttpClient();
                var bytes = await client.GetByteArrayAsync(iconUrl);
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

                    var scaled = Android.Graphics.Bitmap.CreateScaledBitmap(bitmap, newWidth, newHeight, filter: true);
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

    // Assign a unique RequestCode so Android doesn't overwrite the intents with each other
    private PendingIntent CreateActionIntent(string action, int requestCode)
    {
        var context = Android.App.Application.Context;
        var intent = new Intent(context, typeof(MediaActionReceiver));
        intent.SetAction(action);
        return PendingIntent.GetBroadcast(context, requestCode, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }
#endif

    public void SyncOSPlaybackState()
    {
#if WINDOWS
        if (_smtcPlayer != null && _smtcPlayer.SystemMediaTransportControls != null)
        {
            _smtcPlayer.SystemMediaTransportControls.PlaybackStatus = IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
#elif ANDROID
        if (_mediaSession == null) SetupAndroidWidgets(); // Ensure active

        if (_mediaSession != null)
        {
            long actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious | PlaybackState.ActionSeekTo;
            PlaybackStateCode stateCode = IsPlaying ? PlaybackStateCode.Playing : PlaybackStateCode.Paused;

            var stateBuilder = new PlaybackState.Builder()
                .SetActions(actions)
                .SetState(stateCode, (long)CurrentPosition.TotalMilliseconds, 1.0f);

            _mediaSession.SetPlaybackState(stateBuilder.Build());
            UpdateAndroidNotification();
        }
#endif
    }

    private async Task UpdateOSMediaWidgetsAsync(string title, string artist, string iconUrl)
    {
#if WINDOWS
        SetupWindowsWidgets();
        if (_smtcPlayer == null || _smtcPlayer.SystemMediaTransportControls == null) return;

        var displayUpdater = _smtcPlayer.SystemMediaTransportControls.DisplayUpdater;
        displayUpdater.Type = MediaPlaybackType.Music;
        displayUpdater.MusicProperties.Title = title;
        displayUpdater.MusicProperties.Artist = artist;
        displayUpdater.Thumbnail = await LoadWindowsImageAsync(iconUrl);
        displayUpdater.Update();

#elif ANDROID
        SetupAndroidWidgets();
        if (_mediaSession == null) return;

        _currentAndroidArt = await LoadAndroidBitmapAsync(title, iconUrl);

        var metaBuilder = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, title)
            .PutString(MediaMetadata.MetadataKeyArtist, artist)
            .PutLong(MediaMetadata.MetadataKeyDuration, (long)Duration.TotalMilliseconds);

        if (_currentAndroidArt != null)
        {
            metaBuilder.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, _currentAndroidArt);
        }

        _mediaSession.SetMetadata(metaBuilder.Build());
#endif
        SyncOSPlaybackState();
    }

#if ANDROID
    private void UpdateAndroidNotification()
    {
        var context = Android.App.Application.Context;
        if (context == null || _mediaSession == null) return;

        var nm = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (nm == null) return;

#pragma warning disable CA1416 
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel("musicthingy_media", "Media Playback", NotificationImportance.Low);
            nm.CreateNotificationChannel(channel);
        }

        var mediaStyle = new Android.App.Notification.MediaStyle();
        mediaStyle.SetMediaSession(_mediaSession.SessionToken);
        mediaStyle.SetShowActionsInCompactView(0, 1, 2);

        var builder = new Android.App.Notification.Builder(context, "musicthingy_media")
            .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)
            .SetContentTitle(CurrentTrack?.Title ?? "Unknown Track")
            .SetContentText(CurrentTrack?.Playlist?.Name ?? "Musicthingy")
            .SetStyle(mediaStyle)
            .SetOngoing(IsPlaying)
            .SetVisibility(NotificationVisibility.Public);

        if (_currentAndroidArt != null)
        {
            builder.SetLargeIcon(_currentAndroidArt);
        }

        var iconPrev = Android.Graphics.Drawables.Icon.CreateWithResource(context, Android.Resource.Drawable.IcMediaPrevious);
        var actionPrev = new Android.App.Notification.Action.Builder(iconPrev, "Previous", CreateActionIntent("MUSIC_ACTION_PREV", 1)).Build();
        builder.AddAction(actionPrev);

        int playPauseRes = IsPlaying ? Android.Resource.Drawable.IcMediaPause : Android.Resource.Drawable.IcMediaPlay;
        var iconPlayPause = Android.Graphics.Drawables.Icon.CreateWithResource(context, playPauseRes);
        var actionPlayPause = new Android.App.Notification.Action.Builder(iconPlayPause, IsPlaying ? "Pause" : "Play", CreateActionIntent("MUSIC_ACTION_PLAY_PAUSE", 2)).Build();
        builder.AddAction(actionPlayPause);

        var iconNext = Android.Graphics.Drawables.Icon.CreateWithResource(context, Android.Resource.Drawable.IcMediaNext);
        var actionNext = new Android.App.Notification.Action.Builder(iconNext, "Next", CreateActionIntent("MUSIC_ACTION_NEXT", 3)).Build();
        builder.AddAction(actionNext);

        var notification = builder.Build();

        AndroidMediaService.CurrentNotification = notification;

        // CRITICAL FIX: Only call StartForegroundService ONCE.
        // If it's already running, we just update it using nm.Notify below.
        if (!AndroidMediaService.IsRunning)
        {
            var serviceIntent = new Intent(context, typeof(AndroidMediaService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(serviceIntent);
            else
                context.StartService(serviceIntent);
        }

        // Force UI update immediately whether it's a new service or already running
        nm.Notify(1, notification);
#pragma warning restore CA1416
    }
#endif

#if ANDROID
    private Android.OS.PowerManager.WakeLock? _wakeLock;
    private Android.Net.Wifi.WifiManager.WifiLock? _wifiLock;
#endif

    public void AcquireTransientWakeLock()
    {
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;

            if (_wakeLock == null)
            {
                var pm = (Android.OS.PowerManager?)context.GetSystemService(Android.Content.Context.PowerService);
                _wakeLock = pm?.NewWakeLock(Android.OS.WakeLockFlags.Partial, "Musicthingy::Wakelock");
                _wakeLock?.SetReferenceCounted(false);
            }
            _wakeLock?.Acquire(10 * 60 * 1000L);

            if (_wifiLock == null)
            {
                var wm = (Android.Net.Wifi.WifiManager?)context.GetSystemService(Android.Content.Context.WifiService);
                _wifiLock = wm?.CreateWifiLock(Android.Net.WifiMode.FullHighPerf, "Musicthingy::WifiLock");
                _wifiLock?.SetReferenceCounted(false);
            }
            _wifiLock?.Acquire();
        }
        catch { }
#endif
    }

    public void ReleaseTransientWakeLock()
    {
#if ANDROID
        try
        {
            if (_wakeLock?.IsHeld == true) _wakeLock.Release();
            if (_wifiLock?.IsHeld == true) _wifiLock.Release();
        }
        catch { }
#endif
    }
}

// -------------------------------------------------------------
// NATIVE ANDROID FOREGROUND SERVICE & BROADCAST RECEIVERS
// -------------------------------------------------------------
#if ANDROID
[Service(ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaPlayback, Exported = false)]
public class AndroidMediaService : Service
{
    public static bool IsRunning { get; private set; }
    public static Android.App.Notification? CurrentNotification { get; set; }

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
            // CRITICAL FIX: If Android resurrected the service from memory, CurrentNotification is null.
            // We MUST create a fallback notification to satisfy Android's rules, otherwise it crashes.
            if (CurrentNotification == null)
            {
                var nm = (global::Android.App.NotificationManager?)GetSystemService(global::Android.Content.Context.NotificationService);
                if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
                {
                    var channel = new global::Android.App.NotificationChannel("musicthingy_media", "Media Playback", global::Android.App.NotificationImportance.Low);
                    nm?.CreateNotificationChannel(channel);
                }
                CurrentNotification = new global::Android.App.Notification.Builder(this, "musicthingy_media")
                    .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
                    .SetContentTitle("Musicthingy")
                    .Build();
            }

#pragma warning disable CA1416 
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.Q)
                StartForeground(1, CurrentNotification, global::Android.Content.PM.ForegroundService.TypeMediaPlayback);
            else
                StartForeground(1, CurrentNotification);
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AndroidMediaService] Failed to StartForeground: {ex}");
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        base.OnDestroy();
    }
}

[BroadcastReceiver(Exported = true)] // Exported allows SystemUI to ping our app successfully
[IntentFilter(new[] { "MUSIC_ACTION_PLAY_PAUSE", "MUSIC_ACTION_NEXT", "MUSIC_ACTION_PREV" })]
public class MediaActionReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        // Must push back to MainThread to execute ViewModel Commands properly!
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (intent?.Action == "MUSIC_ACTION_PLAY_PAUSE")
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Musicthingy.Models.MediaControlMessage { Action = "PlayPause" });
            else if (intent?.Action == "MUSIC_ACTION_NEXT")
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Musicthingy.Models.MediaControlMessage { Action = "Next" });
            else if (intent?.Action == "MUSIC_ACTION_PREV")
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Musicthingy.Models.MediaControlMessage { Action = "Previous" });
        });
    }
}
#endif