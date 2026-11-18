using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Musicthingy.Services;

public static class LyricsClient
{
    public static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Budget for the WHOLE fallback chain, not per request: without it a track
    // with no lyrics ran up to seven sequential 15s timeouts (over 100s) on a
    // bad connection, with the spinner up the entire time.
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(30);

    // A short title is only accepted as an EXACT match: "I", "Love" and "One"
    // used to substring-match titles of completely unrelated songs. Substring
    // hits are allowed from this many characters (or for any multi-word
    // title, which is specific enough to be meaningful).
    private const int MinSubstringTitleLength = 6;

    // Noise in brackets: "(Official Video)", "[Lyrics]", "(Remastered 2011)",
    // "(Live)". The bracket body is matched with [^\)\]] so one bracket can
    // never swallow the next one - "Song (feat. X) (Official Video)" used to
    // lose "(feat. X)" as well.
    private static readonly Regex BracketNoiseRegex = new(
        @"[\(\[][^\)\]]*?(?:official|soundtrack|ost|lyric|video|audio|music|theme|remaster(?:ed)?|live|acoustic|instrumental|demo)\b[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Descriptor tail after a dash: "Bohemian Rhapsody - Remastered 2011".
    // Dropping the tail (not the head!) keeps the song name.
    private static readonly Regex DescriptorSuffixRegex = new(
        @"\s+-\s+(?:remaster(?:ed)?(?:\s*\d{4})?|live(?:\s+at\s+.*)?|radio\s+edit|edit|version|mix|acoustic|instrumental|demo|mono|stereo)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static LyricsClient()
    {
        SharedClient.DefaultRequestHeaders.Add("User-Agent", "Musicthingy/1.5 (https://github.com/avinaces/MusicthingyV2)");
    }

    /// <summary>
    /// Master fetching algorithm combining YT Music metadata inference, LRCLIB 4-tier fallback, and YT Native fallback.
    /// </summary>
    /// <param name="cancellationToken">Cancels the search (e.g. the user started another one).</param>
    public static async Task<string?> FetchLyricsAsync(string url, string title, string artist, bool preferTimed = true, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(OverallTimeout);

        try
        {
            return await FetchLyricsCoreAsync(url, title, artist, preferTimed, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own budget expired. Report it as "no lyrics found" instead of
            // an error - the user only needs to know the search is over.
            Log($"overall budget of {OverallTimeout.TotalSeconds:F0}s expired");
            return null;
        }
    }

    private static async Task<string?> FetchLyricsCoreAsync(string url, string title, string artist, bool preferTimed, CancellationToken ct)
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
                    using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    using var playerRes = await SharedClient.PostAsync("https://music.youtube.com/youtubei/v1/player", content, ct);
                    if (playerRes.IsSuccessStatusCode)
                    {
                        using var playerDoc = JsonDocument.Parse(await playerRes.Content.ReadAsStringAsync(ct));
                        if (playerDoc.RootElement.TryGetProperty("videoDetails", out var details))
                        {
                            if (string.IsNullOrWhiteSpace(title) && details.TryGetProperty("title", out var tEl))
                                title = tEl.GetString() ?? title;
                            if (string.IsNullOrWhiteSpace(artist) && details.TryGetProperty("author", out var aEl))
                                artist = (aEl.GetString() ?? "").Replace(" - Topic", "");
                        }
                    }
                    else
                    {
                        Log($"yt player metadata -> HTTP {(int)playerRes.StatusCode}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"yt player metadata failed: {ex.Message}");
                }
            }
        }

        string cleanedTitle = CleanTrackTitle(title, artist);

        // 2. Try LRCLIB (4-Tier Fallback Strategy)
        try
        {
            string? lrcResult = await SearchLrcLibAsync(title, cleanedTitle, artist, preferTimed, ct);
            if (!string.IsNullOrWhiteSpace(lrcResult)) return lrcResult;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"lrclib lookup failed: {ex.Message}");
        }

        // 3. Fallback to Native YouTube Timed/Static Lyrics
        if (!string.IsNullOrEmpty(videoId))
        {
            try
            {
                var payload = new { context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20231214.00.00", hl = "en" } }, videoId = videoId };
                using var nextContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var nextRes = await SharedClient.PostAsync("https://music.youtube.com/youtubei/v1/next", nextContent, ct);
                string nextJson = await nextRes.Content.ReadAsStringAsync(ct);

                var match = Regex.Match(nextJson, @"\""browseId\"":\s*\""(MPLYt[\w-]+)\""");
                if (match.Success)
                {
                    string browseId = match.Groups[1].Value;
                    var browsePayload = new { context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20231214.00.00", hl = "en" } }, browseId = browseId };
                    using var browseContent = new StringContent(JsonSerializer.Serialize(browsePayload), Encoding.UTF8, "application/json");
                    using var browseRes = await SharedClient.PostAsync("https://music.youtube.com/youtubei/v1/browse", browseContent, ct);
                    string browseJson = await browseRes.Content.ReadAsStringAsync(ct);

                    var timedLyrics = new List<(long timeMs, string text)>();
                    using var browseDoc = JsonDocument.Parse(browseJson);
                    ExtractYTLyrics(browseDoc.RootElement, timedLyrics);

                    if (timedLyrics.Count > 0)
                    {
                        timedLyrics.Sort((a, b) => a.timeMs.CompareTo(b.timeMs));

                        if (!preferTimed)
                        {
                            return string.Join(Environment.NewLine, timedLyrics.Select(x => x.text));
                        }

                        var sb = new StringBuilder();
                        foreach (var l in timedLyrics)
                        {
                            TimeSpan t = TimeSpan.FromMilliseconds(l.timeMs);
                            // TotalMinutes, not Minutes: Minutes wraps modulo 60
                            // on tracks that are an hour or longer.
                            sb.AppendLine($"[{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}] {l.text}");
                        }
                        return sb.ToString();
                    }

                    // Static (untimed) lyrics live in description.runs. Reading
                    // them from the already-parsed JSON avoids Regex.Unescape,
                    // which unescapes REGEX escapes rather than JSON ones: a
                    // "\/" (common in URLs and lyrics) threw, the exception was
                    // swallowed, and the lyrics were silently lost. It also
                    // dropped multi-run descriptions.
                    string? descriptionText = ExtractDescriptionText(browseDoc.RootElement);
                    if (!string.IsNullOrWhiteSpace(descriptionText))
                        return descriptionText;
                }
                else
                {
                    Log("yt lyrics: no MPLYt browse id in next response");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"yt lyrics failed: {ex.Message}");
            }
        }
        return null;
    }

    private static async Task<string?> SearchLrcLibAsync(string title, string cleanedTitle, string artist, bool preferTimed, CancellationToken ct)
    {
        string targetProp = preferTimed ? "syncedLyrics" : "plainLyrics";
        string fallbackProp = preferTimed ? "plainLyrics" : "syncedLyrics";

        // An exact match that only has the OTHER lyrics kind (plain when we
        // asked for synced) is still the right song. Keep it as a fallback so a
        // fuzzy match for a different song cannot beat it, and so we do not
        // report "no lyrics" while holding exact plain lyrics.
        string? fallbackLyrics = null;

        // TIER 1 + 2: exact /api/get matches. The endpoint REQUIRES
        // artist_name (without it every call just returns 400), so skip it
        // when the artist is unknown instead of burning two round-trips.
        if (!string.IsNullOrWhiteSpace(artist))
        {
            var tier1 = await RequestExactLrcLibAsync(title, artist, targetProp, fallbackProp, ct);
            if (tier1.Preferred != null) return tier1.Preferred;
            fallbackLyrics ??= tier1.Fallback;

            if (!string.Equals(cleanedTitle, title, StringComparison.OrdinalIgnoreCase))
            {
                var tier2 = await RequestExactLrcLibAsync(cleanedTitle, artist, targetProp, fallbackProp, ct);
                if (tier2.Preferred != null) return tier2.Preferred;
                fallbackLyrics ??= tier2.Fallback;
            }
        }

        // TIER 3: Fuzzy Search (Cleaned Title + Artist) - same artist required,
        // otherwise the first same-titled song by ANY artist won.
        string? tier3 = await RequestLrcLibSearchAsync(
            $"{cleanedTitle} {artist}", cleanedTitle, artist, targetProp, requireArtistMatch: true, ct);
        if (tier3 != null) return tier3;

        // TIER 4: Fuzzy Search (Cleaned Title ONLY) - recall over precision.
        string? tier4 = await RequestLrcLibSearchAsync(
            cleanedTitle, cleanedTitle, artist, targetProp, requireArtistMatch: false, ct);
        if (tier4 != null) return tier4;

        return fallbackLyrics;
    }

    private static async Task<(string? Preferred, string? Fallback)> RequestExactLrcLibAsync(
        string trackName, string artist, string targetProp, string fallbackProp, CancellationToken ct)
    {
        try
        {
            using var response = await SharedClient.GetAsync(
                $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(trackName)}&artist_name={Uri.EscapeDataString(artist)}",
                ct);

            if (!response.IsSuccessStatusCode)
            {
                // 404 is the normal "no exact match"; 429 and 5xx are worth a log.
                if ((int)response.StatusCode != 404)
                    Log($"lrclib get '{trackName}' -> HTTP {(int)response.StatusCode}");
                return (null, null);
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);

            return (ReadLyricsProperty(doc.RootElement, targetProp),
                    ReadLyricsProperty(doc.RootElement, fallbackProp));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"lrclib get '{trackName}' failed: {ex.Message}");
            return (null, null);
        }
    }

    private static async Task<string?> RequestLrcLibSearchAsync(
        string query, string cleanedTitle, string artist, string targetProp, bool requireArtistMatch, CancellationToken ct)
    {
        try
        {
            using var response = await SharedClient.GetAsync(
                $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}", ct);

            if (!response.IsSuccessStatusCode)
            {
                Log($"lrclib search '{query}' -> HTTP {(int)response.StatusCode}");
                return null;
            }

            return ParseLrclibSearch(await response.Content.ReadAsStringAsync(ct), cleanedTitle, artist, targetProp, requireArtistMatch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"lrclib search '{query}' failed: {ex.Message}");
            return null;
        }
    }

    private static string CleanTrackTitle(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        string cleaned = title;

        cleaned = BracketNoiseRegex.Replace(cleaned, "");

        // "Artist - Song": drop the artist prefix only when we actually know
        // the artist. The old code always took the LAST dash segment, which
        // turned "Bohemian Rhapsody - Remastered 2011" into "Remastered 2011".
        int dash = cleaned.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && !string.IsNullOrWhiteSpace(artist) &&
            cleaned[..dash].Trim().Equals(artist.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[(dash + 3)..];
        }

        // Strip a descriptor tail ("- Remastered 2011", "- Live at Wembley")
        // from what is left, keeping the song name.
        cleaned = DescriptorSuffixRegex.Replace(cleaned, "");

        cleaned = Regex.Replace(cleaned, @"^(track\s*\d+|\d+)[\s\.\-\:]+", "", RegexOptions.IgnoreCase);
        if (cleaned.Contains(" | ")) cleaned = cleaned.Split(" | ").First();

        return cleaned.Trim();
    }

    private static bool TitleMatches(string resultTitle, string cleanTitle)
    {
        if (string.IsNullOrWhiteSpace(resultTitle) || string.IsNullOrWhiteSpace(cleanTitle)) return false;

        string rTitle = NormalizeForMatch(resultTitle);
        string cTitle = NormalizeForMatch(cleanTitle);

        if (rTitle.Length == 0 || cTitle.Length == 0) return false;
        if (string.Equals(rTitle, cTitle, StringComparison.Ordinal)) return true;

        // Substring matching needs real words at both ends: "I" or "Love" used
        // to match any title that merely contained the letters.
        bool rIsShorter = rTitle.Length <= cTitle.Length;
        string shorter = rIsShorter ? rTitle : cTitle;
        string longer = rIsShorter ? cTitle : rTitle;
        if (shorter.Length < MinSubstringTitleLength && !shorter.Contains(' ')) return false;

        return Regex.IsMatch(longer, $@"(?:^|\s){Regex.Escape(shorter)}(?:\s|$)");
    }

    private static bool ArtistMatches(string resultArtist, string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return true; // nothing to compare with
        if (string.IsNullOrWhiteSpace(resultArtist)) return false;

        string rArtist = NormalizeForMatch(resultArtist);
        string cArtist = NormalizeForMatch(artist);
        if (rArtist.Length == 0 || cArtist.Length == 0) return true;

        return rArtist.Contains(cArtist, StringComparison.Ordinal) ||
               cArtist.Contains(rArtist, StringComparison.Ordinal);
    }

    /// <summary>Lowercase, punctuation to spaces, single spaces, trimmed.</summary>
    private static string NormalizeForMatch(string value)
    {
        string normalized = Regex.Replace(value.ToLowerInvariant(), @"[^\w\s]", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static string? ReadLyricsProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;

        string? value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ParseLrclibSearch(string jsonStr, string cleanedTitle, string artist, string targetProp, bool requireArtistMatch)
    {
        using var doc = JsonDocument.Parse(jsonStr);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        string? titleOnlyMatch = null;

        foreach (var result in doc.RootElement.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object)
                continue;

            string? lrc = ReadLyricsProperty(result, targetProp);
            if (lrc == null)
                continue;

            string foundTitle = result.TryGetProperty("trackName", out var tn) && tn.ValueKind == JsonValueKind.String
                ? tn.GetString() ?? ""
                : "";

            if (!TitleMatches(foundTitle, cleanedTitle))
                continue;

            string foundArtist = result.TryGetProperty("artistName", out var an) && an.ValueKind == JsonValueKind.String
                ? an.GetString() ?? ""
                : "";

            if (ArtistMatches(foundArtist, artist))
                return lrc;

            titleOnlyMatch ??= lrc;
        }

        return requireArtistMatch ? null : titleOnlyMatch;
    }

    // Static lyrics: the "description" object holds an array of text runs.
    private static string? ExtractDescriptionText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                string? found = ExtractDescriptionText(item);
                if (found != null) return found;
            }
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty("description", out var description) &&
            description.ValueKind == JsonValueKind.Object &&
            description.TryGetProperty("runs", out var runs) &&
            runs.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var run in runs.EnumerateArray())
            {
                if (run.ValueKind == JsonValueKind.Object &&
                    run.TryGetProperty("text", out var textEl) &&
                    textEl.ValueKind == JsonValueKind.String)
                {
                    sb.Append(textEl.GetString());
                }
            }

            if (sb.Length > 0) return sb.ToString();
        }

        foreach (var prop in element.EnumerateObject())
        {
            string? found = ExtractDescriptionText(prop.Value);
            if (found != null) return found;
        }

        return null;
    }

    private static void ExtractYTLyrics(JsonElement element, List<(long timeMs, string text)> lyrics)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ExtractYTLyrics(item, lyrics);
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            // YouTube usually sends the timestamp as a string, but some
            // responses use a number - GetString() on a number throws and the
            // whole lyrics payload was silently discarded.
            string? startStr = null;
            if (element.TryGetProperty("startTimeMs", out var st1)) startStr = ReadJsonNumberOrString(st1);
            else if (element.TryGetProperty("startTime", out var st2)) startStr = ReadJsonNumberOrString(st2);

            if (startStr != null && long.TryParse(startStr, out long ms) && ms >= 0 && ms <= int.MaxValue)
            {
                string text = "";
                if (element.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.String)
                    text = wordsEl.GetString() ?? "";
                else if (element.TryGetProperty("lyric", out var lyricEl) && lyricEl.ValueKind == JsonValueKind.String)
                    text = lyricEl.GetString() ?? "";

                if (string.IsNullOrEmpty(text) && element.TryGetProperty("runs", out var runsEl) && runsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var run in runsEl.EnumerateArray())
                        if (run.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                            text += tEl.GetString();
                }

                if (!string.IsNullOrWhiteSpace(text)) lyrics.Add((ms, text.Trim()));
            }

            foreach (var prop in element.EnumerateObject()) ExtractYTLyrics(prop.Value, lyrics);
        }
    }

    private static string? ReadJsonNumberOrString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        _ => null,
    };

    private static void Log(string message) => Debug.WriteLine($"[LyricsClient] {message}");
}
