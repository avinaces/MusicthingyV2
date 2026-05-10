using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Models;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] public partial bool IsFetchingLyrics { get; set; }
    [ObservableProperty] public partial bool HasNoLyrics { get; set; }
    [ObservableProperty] public partial bool HasTimedLyrics { get; set; }
    [ObservableProperty] public partial bool HasStaticLyrics { get; set; }

    public ObservableCollection<TimedLyric> ActiveTimedLyrics { get; } = new();

    private void ParseLyrics(string? rawLyrics)
    {
        ActiveTimedLyrics.Clear();
        HasNoLyrics = string.IsNullOrWhiteSpace(rawLyrics);
        HasTimedLyrics = false;
        HasStaticLyrics = false;

        if (HasNoLyrics || rawLyrics == null) return;

        var lines = rawLyrics.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var regex = new Regex(@"\[(\d{2}):(\d{2})\.(\d{2,3})\]\s*(.*)");

        bool hasTimed = false;
        var lyricMap = new Dictionary<TimeSpan, string>();

        foreach (var line in lines)
        {
            var match = regex.Match(line);
            if (match.Success)
            {
                hasTimed = true;
                int m = int.Parse(match.Groups[1].Value);
                int s = int.Parse(match.Groups[2].Value);
                int ms = int.Parse(match.Groups[3].Value);
                if (match.Groups[3].Value.Length == 2) ms *= 10;

                string text = match.Groups[4].Value.Trim();
                TimeSpan time = new TimeSpan(0, 0, m, s, ms);

                // 1. Filter out fake "metadata" disguising as lyrics in the first 20 seconds
                // Matches lines like: "Lời: Name", "Nhạc: Name", "Lyrics: Name", "作词： Name"
                if (time.TotalSeconds < 20 && Regex.IsMatch(text, @"^([\p{L}]+\s*){1,3}[:：].*"))
                {
                    continue;
                }

                // 2. Filter out interleaved translations
                // Check if a lyric already exists within 100ms of this timestamp.
                bool foundExisting = false;
                TimeSpan existingKey = TimeSpan.Zero;

                foreach (var k in lyricMap.Keys)
                {
                    if (Math.Abs((k - time).TotalMilliseconds) < 100)
                    {
                        foundExisting = true;
                        existingKey = k;
                        break;
                    }
                }

                if (foundExisting)
                {
                    lyricMap.Remove(existingKey);
                }

                // Insert the new line. By overwriting close/duplicate timestamps, 
                // we strip away the bilingual spam and keep a single clean line!
                lyricMap[time] = string.IsNullOrEmpty(text) ? " " : text;
            }
        }

        if (hasTimed)
        {
            HasTimedLyrics = true;

            // Add them to the UI in chronological order
            foreach (var kvp in lyricMap.OrderBy(k => k.Key))
            {
                ActiveTimedLyrics.Add(new TimedLyric { Time = kvp.Key, Text = kvp.Value });
            }
        }
        else
        {
            HasStaticLyrics = true;
        }
    }

    private void SyncActiveLyric(TimeSpan position)
    {
        if (!HasTimedLyrics || ActiveTimedLyrics.Count == 0) return;

        TimedLyric? active = null;
        foreach (var lyric in ActiveTimedLyrics)
        {
            if (position.TotalMilliseconds >= (lyric.Time.TotalMilliseconds - 300))
                active = lyric;
            else
                break;
        }

        foreach (var lyric in ActiveTimedLyrics)
        {
            bool shouldBeActive = (lyric == active);
            if (lyric.IsActive != shouldBeActive)
            {
                lyric.IsActive = shouldBeActive;
            }
        }
    }
    [RelayCommand]
    private async Task FindLyricsAsync()
    {
        if (CurrentTrack == null || string.IsNullOrEmpty(CurrentTrack.Url)) return;

        IsFetchingLyrics = true;
        try
        {
            string? newLyrics = await FetchLyricsLogicAsync(CurrentTrack);
            if (!string.IsNullOrEmpty(newLyrics))
            {
                CurrentTrack.Lyrics = newLyrics;
                ParseLyrics(newLyrics);
                RequestSaveLibrary();
            }
            else
            {
                await ShowConfirmAsync("Lyrics", "Could not find any lyrics for this track.", "OK", ModalBackgroundTapBehavior.Ignore);
            }
        }
        catch (Exception ex)
        {
            await ShowConfirmAsync("Error", $"Failed to fetch lyrics: {ex.Message}", "OK", ModalBackgroundTapBehavior.Ignore);
        }
        finally
        {
            IsFetchingLyrics = false;
        }
    }

    private string CleanTrackTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        string cleaned = title;

        // 1. Bracketed junk
        cleaned = Regex.Replace(cleaned, @"[\(\[].*?(official|soundtrack|ost|lyric|video|audio|music|theme).*?[\)\]]", "", RegexOptions.IgnoreCase);

        // 2. " - " Split (Keep right side)
        if (cleaned.Contains(" - ")) cleaned = cleaned.Split(" - ").Last();

        // 3. Leading numbers
        cleaned = Regex.Replace(cleaned, @"^(track\s*\d+|\d+)[\s\.\-\:]+", "", RegexOptions.IgnoreCase);

        // 4. " | " Split (Keep left side)
        if (cleaned.Contains(" | ")) cleaned = cleaned.Split(" | ").First();

        return cleaned.Trim();
    }

    private bool TitleMatches(string resultTitle, string cleanTitle)
    {
        if (string.IsNullOrWhiteSpace(resultTitle) || string.IsNullOrWhiteSpace(cleanTitle)) return false;

        string rTitle = Regex.Replace(resultTitle.ToLowerInvariant(), @"[^\w\s]", "").Trim();
        string cTitle = Regex.Replace(cleanTitle.ToLowerInvariant(), @"[^\w\s]", "").Trim();

        if (string.IsNullOrWhiteSpace(rTitle) || string.IsNullOrWhiteSpace(cTitle)) return false;
        return cTitle.Contains(rTitle) || rTitle.Contains(cTitle);
    }

    private string? ParseLrclibSearch(string jsonStr, string cleanedTitle)
    {
        using var doc = JsonDocument.Parse(jsonStr);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in doc.RootElement.EnumerateArray())
            {
                if (result.TryGetProperty("syncedLyrics", out var syncedObj) && syncedObj.ValueKind == JsonValueKind.String)
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

    private async Task<string?> FetchLyricsLogicAsync(PlaylistItem track)
    {
        string title = track.Title ?? "";
        string artist = "";
        string videoId = "";

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.Add("User-Agent", "Musicthingy/1.3 (https://github.com/Musicthingy)");

        // 1. Clean Metadata Extraction via YouTube Music API
        if (!string.IsNullOrEmpty(track.Url) && (track.Url.Contains("youtube.com") || track.Url.Contains("youtu.be")))
        {
            var vid = YoutubeExplode.Videos.VideoId.TryParse(track.Url);
            if (vid != null)
            {
                videoId = vid.Value.Value;
                try
                {
                    var payload = new { context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20231214.00.00", hl = "en" } }, videoId = videoId };
                    var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

                    var playerRes = await http.PostAsync("https://music.youtube.com/youtubei/v1/player", content);
                    if (playerRes.IsSuccessStatusCode)
                    {
                        using var playerDoc = JsonDocument.Parse(await playerRes.Content.ReadAsStringAsync());
                        if (playerDoc.RootElement.TryGetProperty("videoDetails", out var details))
                        {
                            if (details.TryGetProperty("title", out var tEl)) title = tEl.GetString() ?? title;
                            if (details.TryGetProperty("author", out var aEl)) artist = (aEl.GetString() ?? "").Replace(" - Topic", "");
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
            // TIER 1: Exact Match (Raw Title)
            var r1 = await http.GetAsync($"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}");
            if (r1.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await r1.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("syncedLyrics", out var synced) && !string.IsNullOrWhiteSpace(synced.GetString()))
                    return synced.GetString();
            }

            // TIER 2: Exact Match (Cleaned Title)
            if (!cleanedTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
            {
                var r2 = await http.GetAsync($"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(cleanedTitle)}&artist_name={Uri.EscapeDataString(artist)}");
                if (r2.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await r2.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("syncedLyrics", out var synced) && !string.IsNullOrWhiteSpace(synced.GetString()))
                        return synced.GetString();
                }
            }

            // TIER 3: Fuzzy Search (Cleaned Title + Artist)
            var r3 = await http.GetAsync($"https://lrclib.net/api/search?q={Uri.EscapeDataString($"{cleanedTitle} {artist}")}");
            if (r3.IsSuccessStatusCode)
            {
                string? res = ParseLrclibSearch(await r3.Content.ReadAsStringAsync(), cleanedTitle);
                if (res != null) return res;
            }

            // TIER 4: Fuzzy Search (Cleaned Title ONLY)
            var r4 = await http.GetAsync($"https://lrclib.net/api/search?q={Uri.EscapeDataString(cleanedTitle)}");
            if (r4.IsSuccessStatusCode)
            {
                string? res = ParseLrclibSearch(await r4.Content.ReadAsStringAsync(), cleanedTitle);
                if (res != null) return res;
            }
        }
        catch { }

        // 3. Fallback to Native YouTube Timed/Static Lyrics
        if (!string.IsNullOrEmpty(videoId))
        {
            try
            {
                var payload = new { context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20231214.00.00", hl = "en" } }, videoId = videoId };
                var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

                var nextRes = await http.PostAsync("https://music.youtube.com/youtubei/v1/next", content);
                var nextJson = await nextRes.Content.ReadAsStringAsync();

                var match = Regex.Match(nextJson, @"\""browseId\"":\s*\""(MPLYt[\w-]+)\""");
                if (match.Success)
                {
                    string browseId = match.Groups[1].Value;
                    var browsePayload = new { context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20231214.00.00", hl = "en" } }, browseId = browseId };
                    var browseContent = new StringContent(JsonSerializer.Serialize(browsePayload), System.Text.Encoding.UTF8, "application/json");

                    var browseRes = await http.PostAsync("https://music.youtube.com/youtubei/v1/browse", browseContent);
                    var browseJson = await browseRes.Content.ReadAsStringAsync();

                    var timedLyrics = new List<(int timeMs, string text)>();
                    using var browseDoc = JsonDocument.Parse(browseJson);
                    ExtractYTLyrics(browseDoc.RootElement, timedLyrics);

                    if (timedLyrics.Count > 0)
                    {
                        timedLyrics.Sort((a, b) => a.timeMs.CompareTo(b.timeMs));
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

    private void ExtractYTLyrics(JsonElement element, List<(int timeMs, string text)> lyrics)
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