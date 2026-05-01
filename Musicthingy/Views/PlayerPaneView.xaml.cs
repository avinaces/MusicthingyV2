using CommunityToolkit.Mvvm.Messaging;
using System.Diagnostics;

namespace Musicthingy.Views;

public partial class PlayerPaneView : ContentView
{
    public PlayerPaneView()
    {
        InitializeComponent();

        // Listen for the Stop message from the ViewModel
        WeakReferenceMessenger.Default.Register<Models.MediaControlMessage>(this, (r, m) =>
        {
            if (m.Action == "StartPlayback")
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    // Seek first while the media is paused
                    if (m.Position.HasValue && m.Position.Value.TotalSeconds > 0)
                    {
                        MainPlayer.SeekTo(m.Position.Value);
                    }
                    // THEN play, forcing a fresh, high-speed HTTP Range Request
                    MainPlayer.Play();
                });
            }
            // NEW: Handles restoring app state without playing
            else if (m.Action == "RestorePlayback")
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (m.Position.HasValue && m.Position.Value.TotalSeconds > 0)
                    {
                        MainPlayer.SeekTo(m.Position.Value);
                    }
                    // Explicitly pause so it doesn't auto-play on load
                    MainPlayer.Pause();
                });
            }
            else if (m.Action == "Seek" && m.Position.HasValue)
            {
                Debug.WriteLine("Seeking to: " + m.Position.Value);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    MainPlayer.SeekTo(m.Position.Value);
                    MainPlayer.Play();
                });
            }
            else if (m.Action == "Stop")
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    MainPlayer.Stop();
                });
            }

        });
    }

    private void MainPlayer_MediaEnded(object? sender, EventArgs e)
    {
        if (BindingContext is ViewModels.MainViewModel vm)
        {
            if (vm.PlayNextCommand.CanExecute(null))
                vm.PlayNextCommand.Execute(null);
        }
    }

   private void MainPlayer_MediaOpened(object? sender, EventArgs e)
    {
#if WINDOWS
        // Hook into the native CommandManager AFTER the file loads
        if (MainPlayer.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.MediaPlayerElement playerElement)
        {
            var commandManager = playerElement.MediaPlayer.CommandManager;
            var smtc = playerElement.MediaPlayer.SystemMediaTransportControls;

            // Force OS Next/Prev buttons to always be enabled and visible
            commandManager.NextBehavior.EnablingRule = Windows.Media.Playback.MediaCommandEnablingRule.Always;
            commandManager.PreviousBehavior.EnablingRule = Windows.Media.Playback.MediaCommandEnablingRule.Always;
            smtc.IsNextEnabled = true;
            smtc.IsPreviousEnabled = true;

            // Unsubscribe first to prevent duplicate event firing when tracks change
            commandManager.NextReceived -= CommandManager_NextReceived;
            commandManager.PreviousReceived -= CommandManager_PreviousReceived;

            // Subscribe to the CommandManager events
            commandManager.NextReceived += CommandManager_NextReceived;
            commandManager.PreviousReceived += CommandManager_PreviousReceived;
        }
#endif
    }

#if WINDOWS
    private void CommandManager_NextReceived(Windows.Media.Playback.MediaPlaybackCommandManager sender, Windows.Media.Playback.MediaPlaybackCommandManagerNextReceivedEventArgs args)
    {
        args.Handled = true; // Tell Windows we handled this manually
        MainThread.BeginInvokeOnMainThread(() =>
        {
            WeakReferenceMessenger.Default.Send(new Models.MediaControlMessage { Action = "Next" });
        });
    }

    private void CommandManager_PreviousReceived(Windows.Media.Playback.MediaPlaybackCommandManager sender, Windows.Media.Playback.MediaPlaybackCommandManagerPreviousReceivedEventArgs args)
    {
        args.Handled = true; // Tell Windows we handled this manually
        MainThread.BeginInvokeOnMainThread(() =>
        {
            WeakReferenceMessenger.Default.Send(new Models.MediaControlMessage { Action = "Previous" });
        });
    }
#endif

#if WINDOWS
    private void Smtc_ButtonPressed(Windows.Media.SystemMediaTransportControls sender, Windows.Media.SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // OS Media Control buttons fire on a background system thread.
        // We MUST force the message onto the Main UI thread, otherwise the ViewModel Command silently fails.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (args.Button == Windows.Media.SystemMediaTransportControlsButton.Next)
            {
                WeakReferenceMessenger.Default.Send(new Models.MediaControlMessage { Action = "Next" });
            }
            else if (args.Button == Windows.Media.SystemMediaTransportControlsButton.Previous)
            {
                WeakReferenceMessenger.Default.Send(new Models.MediaControlMessage { Action = "Previous" });
            }
        });
    }
#endif
}