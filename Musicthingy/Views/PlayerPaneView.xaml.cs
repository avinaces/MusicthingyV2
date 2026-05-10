using Microsoft.Maui.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;

namespace Musicthingy.Views;

public partial class PlayerPaneView : ContentView
{
    public PlayerPaneView()
    {
        InitializeComponent();

        // Subscribe to Auto-scroll Lyrics
        WeakReferenceMessenger.Default.Register<ActiveLyricChangedMessage>(this, (r, m) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (m.Lyric != null)
                {
                    LyricsCollectionView.ScrollTo(m.Lyric, position: ScrollToPosition.Center, animate: true);
                }
            });
        });
    }
}