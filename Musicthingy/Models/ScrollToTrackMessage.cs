namespace Musicthingy.Models;

public class ScrollToTrackMessage
{
    public PlaylistItem Track { get; }
    public ScrollToTrackMessage(PlaylistItem track)
    {
        Track = track;
    }
}