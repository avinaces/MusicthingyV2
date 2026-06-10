using YoutubeExplode.Videos;

namespace Musicthingy.Helpers;

public static class MediaUrlHelper
{
    public static bool IsYouTubeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        return url.Contains("youtube.com") || url.Contains("youtu.be");
    }

    public static string? GetYouTubeVideoId(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        var videoId = VideoId.TryParse(url);
        return videoId?.Value;
    }

    public static string? GetYouTubeThumbnail(string? url)
    {
        string? id = GetYouTubeVideoId(url);
        // Use mqdefault.jpg which is natively 16:9 (no black bars) instead of hqdefault.jpg
        return id != null ? $"https://img.youtube.com/vi/{id}/mqdefault.jpg" : null;
    }
}