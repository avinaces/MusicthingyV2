namespace Musicthingy.Models;

public class MediaControlMessage
{
    public string Action { get; set; } = string.Empty;
    public TimeSpan? Position { get; set; }

    /// <summary>YouTube video ID for the debug "PlayVideo" action.</summary>
    public string? VideoId { get; set; }

    /// <summary>Optional start offset (seconds) for the debug "PlayVideo" action.</summary>
    public int? StartSeconds { get; set; }
}

public record HardwareVolumeMessage(int VolumeLevel);