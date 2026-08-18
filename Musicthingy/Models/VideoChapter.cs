namespace Musicthingy.Models
{
    public record VideoChapter(TimeSpan Timestamp, string Title);
    public record YouTubePlaylistVideo(string Title, string Url, string? ThumbnailUrl, TimeSpan? Duration);
}