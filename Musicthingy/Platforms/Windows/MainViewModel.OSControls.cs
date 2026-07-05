using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private MediaPlayer? _smtcPlayer;

    partial void CleanupOSMediaPlatform()
    {
    }

    private partial void SyncOSPlaybackStatePlatform()
    {
        if (_smtcPlayer?.SystemMediaTransportControls != null)
        {
            _smtcPlayer.SystemMediaTransportControls.PlaybackStatus = IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
    }

    private async partial Task UpdateOSMediaWidgetsPlatformAsync(string title, string artist, string iconUrl)
    {
        SetupWindowsWidgets();
        if (_smtcPlayer?.SystemMediaTransportControls == null) return;

        SystemMediaTransportControlsDisplayUpdater displayUpdater = _smtcPlayer.SystemMediaTransportControls.DisplayUpdater;
        displayUpdater.Type = MediaPlaybackType.Music;
        displayUpdater.MusicProperties.Title = title;
        displayUpdater.MusicProperties.Artist = artist;
        displayUpdater.Thumbnail = await LoadWindowsImageAsync(iconUrl);
        displayUpdater.Update();

        SyncOSPlaybackStatePlatform();
    }

    private void SetupWindowsWidgets()
    {
        if (_smtcPlayer != null) return;

        _smtcPlayer = new MediaPlayer();
        SystemMediaTransportControls? smtc = _smtcPlayer.SystemMediaTransportControls;
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
                switch (args.Button)
                {
                    case SystemMediaTransportControlsButton.Play:
                    case SystemMediaTransportControlsButton.Pause:
                        TogglePlayCommand.Execute(null);
                        break;
                    case SystemMediaTransportControlsButton.Next:
                        PlayNextCommand.Execute(null);
                        break;
                    case SystemMediaTransportControlsButton.Previous:
                        PlayPreviousCommand.Execute(null);
                        break;
                }
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
                string base64Data = iconUrl.Substring(iconUrl.IndexOf(',') + 1);
                byte[] bytes = Convert.FromBase64String(base64Data);
                string tempPath = Path.Combine(FileSystem.CacheDirectory, "temp_win_art.png");
                await File.WriteAllBytesAsync(tempPath, bytes);
                return RandomAccessStreamReference.CreateFromUri(new Uri("file:///" + tempPath.Replace("\\", "/")));
            }
            if (iconUrl.StartsWith("http"))
            {
                return RandomAccessStreamReference.CreateFromUri(new Uri(iconUrl));
            }
            if (File.Exists(iconUrl))
            {
                return RandomAccessStreamReference.CreateFromUri(new Uri("file:///" + iconUrl.Replace("\\", "/")));
            }
        }
        catch { }
        return null;
    }

    private partial void AcquireTransientWakeLockPlatform() { }
    private partial void ReleaseTransientWakeLockPlatform() { }
}