using Musicthingy.Services;
using System;
using System.Threading.Tasks;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    public void SyncOSPlaybackState() => SyncOSPlaybackStatePlatform();

    private async Task UpdateOSMediaWidgetsAsync(string title, string artist, string iconUrl)
        => await UpdateOSMediaWidgetsPlatformAsync(title, artist, iconUrl);

    public void AcquireTransientWakeLock() => AcquireTransientWakeLockPlatform();

    public void ReleaseTransientWakeLock() => ReleaseTransientWakeLockPlatform();

    private partial void SyncOSPlaybackStatePlatform();
    private partial Task UpdateOSMediaWidgetsPlatformAsync(string title, string artist, string iconUrl);
    private partial void AcquireTransientWakeLockPlatform();
    private partial void ReleaseTransientWakeLockPlatform();

    private void HandleVolumeChange(int volumeLevel)
    {
        if (SettingsManager.IsRestoringState) return;

        if (_settingsManager.PauseAtZeroVolume.Value)
        {
            if (volumeLevel <= 0 && IsPlaying)
            {
                Pause();
                _pausedByZeroVolume = true;
            }
            else if (volumeLevel > 0 && !IsPlaying && _pausedByZeroVolume)
            {
                _pausedByZeroVolume = false;
                Play();
            }
        }
    }
}