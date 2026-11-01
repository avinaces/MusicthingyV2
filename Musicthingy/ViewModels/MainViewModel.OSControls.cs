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

    // Drops ONLY the CPU wake lock (called once audio is actually rendering;
    // the audio path + ExoPlayer wake mode keep the CPU awake from there).
    public void ReleaseCpuWakeLock() => ReleaseTransientCpuLockPlatform();

    // Full teardown of the OS media surface after the queue finished:
    // media session, notification, foreground service, volume receiver.
    public void TeardownOSMedia() => TeardownOSMediaPlatform();

    private partial void SyncOSPlaybackStatePlatform();
    private partial Task UpdateOSMediaWidgetsPlatformAsync(string title, string artist, string iconUrl);
    private partial void AcquireTransientWakeLockPlatform();
    private partial void ReleaseTransientWakeLockPlatform();
    // Deliberately NO access modifier on these two hooks (not even "private"):
    // a modifier-less partial method is OPTIONAL - on targets that have no
    // implementation part (e.g. the Windows TFM, where the Android file is
    // not compiled) the compiler simply elides the calls above. Adding ANY
    // access modifier (even "private") makes the implementation REQUIRED on
    // every target -> CS8796 build errors.
    partial void ReleaseTransientCpuLockPlatform();
    partial void TeardownOSMediaPlatform();

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