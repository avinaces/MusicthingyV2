using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using System;

namespace Musicthingy.Models;

public partial class TimedLyric : ObservableObject
{
    public TimeSpan Time { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    partial void OnIsActiveChanged(bool value)
    {
        // When this lyric becomes active, send a message to the UI to scroll to it
        if (value)
        {
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new ActiveLyricChangedMessage { Lyric = this });
        }
    }
}

public class ActiveLyricChangedMessage
{
    public TimedLyric? Lyric { get; set; }
}