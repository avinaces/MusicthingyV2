using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Musicthingy.Helpers;

public static class NetworkHelper
{
    // Reusable singleton HttpClient to prevent device socket exhaustion across the app
    private static readonly HttpClient _sharedClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    /// <summary>
    /// Checks if a remote URL is reachable and is actual supported media.
    /// Also attempts to extract metadata (Title / Image) if available.
    /// </summary>
    public static async Task<(bool IsSuccess, string ErrorMessage, string? Title, string? ImageUrl)> CheckMediaUrlAsync(string url, CancellationToken token = default)
    {
        try
        {
            // 1. Check if it's a local file
            if (File.Exists(url))
            {
                // Fallback title to the file name without extension
                string title = Path.GetFileNameWithoutExtension(url);
                return (true, string.Empty, title, null);
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uriResult) ||
                (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
            {
                return (false, "Must be a valid HTTP(S) URL or local file path.", null, null);
            }

            // 2. Handle YouTube specifically (prefer native 16:9 thumbnails)
            if (MediaUrlHelper.IsYouTubeUrl(url))
            {
                string? videoId = MediaUrlHelper.GetYouTubeVideoId(url);
                if (videoId == null)
                    return (false, "Invalid YouTube URL format.", null, null);

                // Prefer maxresdefault (native 16:9) when available, otherwise fall back to mqdefault (also 16:9)
                string maxresUrl = $"https://img.youtube.com/vi/{videoId}/maxresdefault.jpg";
                string mqUrl = $"https://img.youtube.com/vi/{videoId}/mqdefault.jpg";
                string resolvedThumbUrl = mqUrl; // default fallback

                try
                {
                    using var headReq = new HttpRequestMessage(HttpMethod.Head, maxresUrl);
                    using var headResp = await _sharedClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, token);
                    if (headResp.IsSuccessStatusCode)
                    {
                        resolvedThumbUrl = maxresUrl;
                    }
                }
                catch { /* Fall back to mqUrl if maxresdefault 404s or request fails */ }

                // Fetch Title via oEmbed
                string? title = null;
                try
                {
                    string oembedUrl = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(url)}&format=json";
                    using var response = await _sharedClient.GetAsync(oembedUrl, HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.IsSuccessStatusCode)
                    {
                        using var stream = await response.Content.ReadAsStreamAsync(token);
                        using var doc = await JsonDocument.ParseAsync(stream, default, token);
                        title = doc.RootElement.GetProperty("title").GetString();
                    }
                }
                catch { }

                return (true, string.Empty, title, resolvedThumbUrl);
            }

            // 3. Handle standard direct HTTP streams
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await _sharedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

            if (!headResponse.IsSuccessStatusCode)
            {
                return (false, $"Server returned {(int)headResponse.StatusCode} ({headResponse.ReasonPhrase})", null, null);
            }

            // Validate Content-Type so we don't accidentally accept "text/html" web pages like example.com
            string contentType = headResponse.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            string[] validTypes = { 
                "audio/", "video/", 
                "application/ogg", "application/x-mpegurl", 
                "application/vnd.apple.mpegurl", "application/dash+xml", 
                "application/octet-stream" // Often used by raw file servers
            };

            if (!validTypes.Any(t => contentType.StartsWith(t)))
            {
                return (false, $"URL does not point to a supported media stream (Type: {contentType})", null, null);
            }

            // As a fallback for raw urls, extract the filename to use as the title
            string fallbackTitle = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(uriResult.AbsolutePath));
            return (true, string.Empty, fallbackTitle, null);
        }
        catch (TaskCanceledException)
        {
            throw; // Bubble up cancellation from debouncer
        }
        catch (Exception)
        {
            return (false, "Failed to reach URL. Ensure you have connection.", null, null);
        }
    }
}