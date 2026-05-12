using System.Text.Json;
using System.Text.RegularExpressions;

namespace Musicthingy.Services;

public static class LyricsClient
{
    public static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    static LyricsClient()
    {
        SharedClient.DefaultRequestHeaders.Add("User-Agent", "Musicthingy/1.5 (https://github.com/avinaces/MusicthingyV2)");
    }

    /// <summary>
    /// Master fetching algorithm combining YT Music metadata inference, LRCLIB 4-tier fallback, and YT Native fallback.
    /// </summary>
    public static async Task<string?> FetchLyricsAsync(string url, string title, string artist, bool preferTimed = true)
    {
        url ??= string.Empty;
        title ??= string.Empty;
        artist ??= string.Empty;

        string videoId = "";

        // 1. Clean Metadata Extraction via YouTube Music API
        if (!string.IsNullOrEmpty(url) && (url.Contains("youtube.com") || url.Contains("youtu.be")))
        {
            var vid = YoutubeExplode.Videos.VideoId.TryParse(url);
            if (vid != null)
            {
                videoId = vid.Value.Value;
                try
                {
                    var payload = new { context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20231214.00.00", hl = "en" } }, videoId = videoId };
                    var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

                    var playerRes = await SharedClient.PostAsync("https://music.youtube.com/youtubei/v1/player", content);
                    if (playerRes.IsSuccessStatusCode)
                    {
                        using var playerDoc = JsonDocument.Parse(await playerRes.Content.ReadAsStringAsync());
                        if (playerDoc.RootElement.TryGetProperty("videoDetails", out var details))
                        {
                            if (string.IsNullOrWhiteSpace(title) && details.TryGetProperty("title", out var tEl))
                                title = tEl.GetString() ?? title;
                            if (string.IsNullOrWhiteSpace(artist) && details.TryGetProperty("author", out var aEl))
                                artist = (aEl.GetString() ?? "").Replace(" - Topic", "");
                        }
                    }
                }
                catch { }
            }
        }

        string cleanedTitle = CleanTrackTitle(title);

        // 2. Try LRCLIB (4-Tier Fallback Strategy)
        try
        {
            string? lrcResult = await SearchLrcLibAsync(title, cleanedTitle, artist, preferTimed);
            if (!string.IsNullOrWhiteSpace(lrcResult)) return lrcResult;
        }
        catch { }

        // 3. Fallback to Native YouTube Timed/Static Lyrics
        if (!string.IsNullOrEmpty(videoId))
        {
            try
            {
                var payload = new { context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20231214.00.00", hl = "en" } }, videoId = videoId };
                var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

                var nextRes = await SharedClient.PostAsync("https://music.youtube.com/youtubei/v1/next", content);
                var nextJson = await nextRes.Content.ReadAsStringAsync();

                var match = Regex.Match(nextJson, @"\""browseId\"":\s*\""(MPLYt[\w-]+)\""");
                if (match.Success)
                {
                    string browseId = match.Groups[1].Value;
                    var browsePayload = new { context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20231214.00.00", hl = "en" } }, browseId = browseId };
                    var browseContent = new StringContent(JsonSerializer.Serialize(browsePayload), System.Text.Encoding.UTF8, "application/json");

                    var browseRes = await SharedClient.PostAsync("https://music.youtube.com/youtubei/v1/browse", browseContent);
                    var browseJson = await browseRes.Content.ReadAsStringAsync();

                    var timedLyrics = new List<(int timeMs, string text)>();
                    using var browseDoc = JsonDocument.Parse(browseJson);
                    ExtractYTLyrics(browseDoc.RootElement, timedLyrics);

                    if (timedLyrics.Count > 0)
                    {
                        timedLyrics.Sort((a, b) => a.timeMs.CompareTo(b.timeMs));

                        if (!preferTimed)
                        {
                            return string.Join(Environment.NewLine, timedLyrics.Select(x => x.text));
                        }

                        var sb = new System.Text.StringBuilder();
                        foreach (var l in timedLyrics)
                        {
                            TimeSpan t = TimeSpan.FromMilliseconds(l.timeMs);
                            sb.AppendLine($"[{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}] {l.text}");
                        }
                        return sb.ToString();
                    }

                    var staticMatch = Regex.Match(browseJson, @"\""description\"":\{\""runs\"":\[\{\""text\"":\s*\""(.*?)\""\}]");
                    if (staticMatch.Success) return Regex.Unescape(staticMatch.Groups[1].Value);
                }
            }
            catch { }
        }
        return null;
    }

    private static async Task<string?> SearchLrcLibAsync(string title, string cleanedTitle, string artist, bool preferTimed)
    {
        string targetProp = preferTimed ? "syncedLyrics" : "plainLyrics";

        // TIER 1: Exact Match (Raw Title)
        var r1 = await SharedClient.GetAsync($"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}");
        if (r1.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await r1.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty(targetProp, out var lyricsProp) && lyricsProp.ValueKind == JsonValueKind.String)
            {
                string? val = lyricsProp.GetString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }

        // TIER 2: Exact Match (Cleaned Title)
        if (!cleanedTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
        {
            var r2 = await SharedClient.GetAsync($"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(cleanedTitle)}&artist_name={Uri.EscapeDataString(artist)}");
            if (r2.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await r2.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty(targetProp, out var lyricsProp) && lyricsProp.ValueKind == JsonValueKind.String)
                {
                    string? val = lyricsProp.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }
        }

        // TIER 3: Fuzzy Search (Cleaned Title + Artist)
        var r3 = await SharedClient.GetAsync($"https://lrclib.net/api/search?q={Uri.EscapeDataString($"{cleanedTitle} {artist}")}");
        if (r3.IsSuccessStatusCode)
        {
            string? res = ParseLrclibSearch(await r3.Content.ReadAsStringAsync(), cleanedTitle, targetProp);
            if (res != null) return res;
        }

        // TIER 4: Fuzzy Search (Cleaned Title ONLY)
        var r4 = await SharedClient.GetAsync($"https://lrclib.net/api/search?q={Uri.EscapeDataString(cleanedTitle)}");
        if (r4.IsSuccessStatusCode)
        {
            string? res = ParseLrclibSearch(await r4.Content.ReadAsStringAsync(), cleanedTitle, targetProp);
            if (res != null) return res;
        }

        return null;
    }

    private static string CleanTrackTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        string cleaned = title;

        cleaned = Regex.Replace(cleaned, @"[\(\[].*?(official|soundtrack|ost|lyric|video|audio|music|theme).*?[\)\]]", "", RegexOptions.IgnoreCase);
        if (cleaned.Contains(" - ")) cleaned = cleaned.Split(" - ").Last();
        cleaned = Regex.Replace(cleaned, @"^(track\s*\d+|\d+)[\s\.\-\:]+", "", RegexOptions.IgnoreCase);
        if (cleaned.Contains(" | ")) cleaned = cleaned.Split(" | ").First();

        return cleaned.Trim();
    }

    private static bool TitleMatches(string resultTitle, string cleanTitle)
    {
        if (string.IsNullOrWhiteSpace(resultTitle) || string.IsNullOrWhiteSpace(cleanTitle)) return false;

        string rTitle = Regex.Replace(resultTitle.ToLowerInvariant(), @"[^\w\s]", "").Trim();
        string cTitle = Regex.Replace(cleanTitle.ToLowerInvariant(), @"[^\w\s]", "").Trim();

        if (string.IsNullOrWhiteSpace(rTitle) || string.IsNullOrWhiteSpace(cTitle)) return false;
        return cTitle.Contains(rTitle) || rTitle.Contains(cTitle);
    }

    private static string? ParseLrclibSearch(string jsonStr, string cleanedTitle, string targetProp)
    {
        using var doc = JsonDocument.Parse(jsonStr);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in doc.RootElement.EnumerateArray())
            {
                if (result.TryGetProperty(targetProp, out var syncedObj) && syncedObj.ValueKind == JsonValueKind.String)
                {
                    string? lrc = syncedObj.GetString();
                    string foundTitle = result.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "";

                    if (!string.IsNullOrWhiteSpace(lrc) && TitleMatches(foundTitle, cleanedTitle))
                    {
                        return lrc;
                    }
                }
            }
        }
        return null;
    }

    private static void ExtractYTLyrics(JsonElement element, List<(int timeMs, string text)> lyrics)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ExtractYTLyrics(item, lyrics);
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            string? startStr = null;
            if (element.TryGetProperty("startTimeMs", out var st1)) startStr = st1.GetString();
            else if (element.TryGetProperty("startTime", out var st2)) startStr = st2.GetString();

            if (startStr != null && int.TryParse(startStr, out int ms))
            {
                string text = "";
                if (element.TryGetProperty("words", out var wordsEl)) text = wordsEl.GetString() ?? "";
                else if (element.TryGetProperty("lyric", out var lyricEl)) text = lyricEl.GetString() ?? "";

                if (string.IsNullOrEmpty(text) && element.TryGetProperty("runs", out var runsEl) && runsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var run in runsEl.EnumerateArray())
                        if (run.TryGetProperty("text", out var tEl)) text += tEl.GetString();
                }

                if (!string.IsNullOrWhiteSpace(text)) lyrics.Add((ms, text.Trim()));
            }

            foreach (var prop in element.EnumerateObject()) ExtractYTLyrics(prop.Value, lyrics);
        }
    }
}