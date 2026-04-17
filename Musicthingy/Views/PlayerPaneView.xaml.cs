using CommunityToolkit.Mvvm.Messaging;

namespace Musicthingy.Views;

public partial class PlayerPaneView : ContentView
{
    public PlayerPaneView()
    {
        InitializeComponent();

        // Listen for the Stop message from the ViewModel
        WeakReferenceMessenger.Default.Register<Models.MediaControlMessage>(this, (r, m) =>
        {
            if (m.Action == "Stop")
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
        // Hook into the native player AFTER it has loaded the file.
        // Otherwise, MAUI resets IsNextEnabled and IsPreviousEnabled back to false.
        if (MainPlayer.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.MediaPlayerElement playerElement)
        {
            var smtc = playerElement.MediaPlayer.SystemMediaTransportControls;
            
            smtc.IsNextEnabled = true;
            smtc.IsPreviousEnabled = true;
            
            // Unsubscribe first to prevent duplicate events firing if the song changes
            smtc.ButtonPressed -= Smtc_ButtonPressed;
            smtc.ButtonPressed += Smtc_ButtonPressed;
        }
#endif
    }

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