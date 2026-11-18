namespace Musicthingy.Models;

public class MediaControlMessage
{
    public string Action { get; set; } = string.Empty;
    public TimeSpan? Position { get; set; }

    /// <summary>YouTube video ID for the debug "PlayVideo" action.</summary>
    public string? VideoId { get; set; }

    /// <summary>Optional start offset (seconds) for the debug "PlayVideo" action.</summary>
    public int? StartSeconds { get; set; }

    // External audio file / URI opened via Intent.ActionView
    public string? Uri { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? ArtworkPath { get; set; }
    public long DurationMs { get; set; }

    /// <summary>Set to true by MainViewModel when handled immediately.</summary>
    public bool Consumed { get; set; }
}

public record HardwareVolumeMessage(int VolumeLevel);