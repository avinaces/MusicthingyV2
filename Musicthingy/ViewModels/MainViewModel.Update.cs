using System.Diagnostics;
using Musicthingy.Services;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    // How long to wait for the restored session before the prompt may appear.
    private const int UpdateRestorePollMs = 250;
    private const int UpdateRestorePollCount = 80;

    // Grace period after startup so the prompt does not land on top of the
    // player restoring its first track.
    private const int UpdatePromptDelayMs = 2500;

    private const int UpdateModalRetryMs = 1000;
    private const int UpdateModalRetries = 15;

    private bool _updateCheckStarted;

    /// <summary>
    /// Looks for a newer GitHub release once per launch, in the background.
    /// </summary>
    /// <remarks>
    /// Never blocks startup and never reports failure: a phone in a tunnel
    /// simply gets no prompt. Showing the prompt is deferred until the restored
    /// session is up, because <see cref="ShowConfirmAsync"/> owns the single
    /// shared modal - two writers would replace the pending
    /// <c>TaskCompletionSource</c> and leave the first caller awaiting forever.
    /// </remarks>
    public void CheckForUpdatesInBackground()
    {
        if (_updateCheckStarted) return;
        _updateCheckStarted = true;
        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        UpdateInfo? update = await _updateService.GetAvailableUpdateAsync().ConfigureAwait(false);
        if (update == null) return;

        // SettingsManager.IsRestoringState is what PlayVideoByIdAsync waits on
        // too: CurrentTrack is still being replaced by the saved session until
        // the library finishes loading.
        for (int i = 0; i < UpdateRestorePollCount && SettingsManager.IsRestoringState; i++)
            await Task.Delay(UpdateRestorePollMs).ConfigureAwait(false);

        await Task.Delay(UpdatePromptDelayMs).ConfigureAwait(false);

        if (!await WaitForModalSlotAsync().ConfigureAwait(false))
        {
            Debug.WriteLine("[Update] Another dialog stayed open - update prompt skipped until next launch");
            return;
        }

        // The busy check and the modal hand-off happen in the same UI-thread
        // turn, so a dialog that appeared while we waited cannot be replaced.
        bool openRelease = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (IsModalVisible)
            {
                Debug.WriteLine("[Update] Modal became busy - update prompt skipped until next launch");
                return false;
            }

            return await ShowConfirmAsync(
                "Update available",
                $"Musicthingy {update.Tag} is out - you are running {AppInfo.Current.VersionString}.\n\nOpen the release page to download it.",
                "View release",
                ModalBackgroundTapBehavior.Cancel);
        }).ConfigureAwait(false);

        if (!openRelease) return;

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => Launcher.Default.OpenAsync(update.ReleaseUrl));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] Could not open {update.ReleaseUrl}: {ex.Message}");
        }
    }

    /// <summary>
    /// True once the shared modal is free. False when something else kept it
    /// busy for the whole budget - an update prompt must never take over a
    /// dialog the user is looking at.
    /// </summary>
    private async Task<bool> WaitForModalSlotAsync()
    {
        for (int i = 0; i <= UpdateModalRetries; i++)
        {
            bool busy = await MainThread.InvokeOnMainThreadAsync(() => IsModalVisible).ConfigureAwait(false);
            if (!busy) return true;

            await Task.Delay(UpdateModalRetryMs).ConfigureAwait(false);
        }

        return false;
    }
}
