using System.Text.Json.Serialization;

namespace Musicthingy.Models;

// These classes exactly match the JSON file structure
public class PlaylistDto
{
    public string name { get; set; } = string.Empty;
    public string icon { get; set; } = string.Empty;
    // Keep DTO using string to support older JSON files. We'll convert to enum when loading.
    public string iconType { get; set; } = string.Empty;
    public string source { get; set; } = string.Empty;
    public DateTime? createdAt { get; set; }
    public DateTime? updatedAt { get; set; }
    public TimeSpan duration { get; set; }
    public List<PlaylistItemDto> items { get; set; } = new();
    public List<string> tags { get; set; } = new();
}

public class PlaylistItemDto
{
    public string title { get; set; } = string.Empty;
    public string url { get; set; } = string.Empty;
    public int? startTime { get; set; }
    public int? endTime { get; set; }
    public TimeSpan duration { get; set; }
    public string icon { get; set; } = string.Empty;
    public List<string> tags { get; set; } = new();
    public string lyrics { get; set; } = string.Empty;
    public DateTime? lastPlayedAt { get; set; }
}