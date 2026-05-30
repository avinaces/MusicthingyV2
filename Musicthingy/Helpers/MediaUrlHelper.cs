namespace Musicthingy.Helpers;

public static class MediaUrlHelper
{
    public static bool IsYouTubeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        return url.Contains("youtube.com") || url.Contains("youtu.be");
    }
}