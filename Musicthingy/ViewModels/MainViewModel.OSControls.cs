#if WINDOWS
using Windows.Media;
#elif ANDROID
using Android.Media;
using Android.Media.Session;
#endif

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
#endif

#if ANDROID
    private MediaSession? _mediaSession;

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
        
        public override void OnPlay() => MainThread.BeginInvokeOnMainThread(() => _vm.TogglePlayCommand.Execute(null));
        public override void OnPause() => MainThread.BeginInvokeOnMainThread(() => _vm.TogglePlayCommand.Execute(null));
        public override void OnSkipToNext() => MainThread.BeginInvokeOnMainThread(() => _vm.PlayNextCommand.Execute(null));
        public override void OnSkipToPrevious() => MainThread.BeginInvokeOnMainThread(() => _vm.PlayPreviousCommand.Execute(null));
    }
#endif

    private void UpdateOSMediaWidgets(string title, string artist, string iconUrl, bool isPlaying)
    {
#if WINDOWS
        SetupWindowsWidgets();
        if (_smtcPlayer == null) return;

        var smtc = _smtcPlayer.SystemMediaTransportControls;
        if (smtc == null) return;

        smtc.PlaybackStatus = isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

        var displayUpdater = smtc.DisplayUpdater;
        if (displayUpdater != null)
        {
            displayUpdater.Type = MediaPlaybackType.Music;
            displayUpdater.MusicProperties.Title = title;
            displayUpdater.MusicProperties.Artist = artist;
            displayUpdater.Update();
        }

#elif ANDROID
        SetupAndroidWidgets();
        if (_mediaSession == null) return;
        
        var metaBuilder = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, title)
            .PutString(MediaMetadata.MetadataKeyArtist, artist);

        if (!string.IsNullOrEmpty(iconUrl) && System.IO.File.Exists(iconUrl))
        {
            var bitmap = Android.Graphics.BitmapFactory.DecodeFile(iconUrl);
            if (bitmap != null) metaBuilder.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, bitmap);
        }

        _mediaSession.SetMetadata(metaBuilder.Build());

        // Correct usage of Android.Media APIs in modern MAUI
        long actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious;
        PlaybackStateCode stateCode = isPlaying ? PlaybackStateCode.Playing : PlaybackStateCode.Paused;

        var stateBuilder = new PlaybackState.Builder()
            .SetActions(actions)
            .SetState(stateCode, (long)CurrentPosition.TotalMilliseconds, 1.0f);

        _mediaSession.SetPlaybackState(stateBuilder.Build());
#endif
    }
}