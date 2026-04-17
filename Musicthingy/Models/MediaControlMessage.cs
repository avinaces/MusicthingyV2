namespace Musicthingy.Models;

public class MediaControlMessage
{
    public string Action { get; set; } = string.Empty;
    public TimeSpan? Position { get; set; }
}