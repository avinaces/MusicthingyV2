using System.Diagnostics;
using System.Globalization;
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

    // Upload noise: never part of the song title - "(Official Video)", "[4K]",
    // "(Audio)", "(From "Frozen"/Soundtrack Version)". The bracket body is
    // matched with [^\)\]] so one bracket can never swallow the next:
    // "Song (feat. X) (Official Video)" must keep "(feat. X)".
    //
    // Recording-level qualifiers (live, acoustic, remix, instrumental, demo ...)
    // are deliberately NOT in this list. They select a DIFFERENT recording, so
    // they survive cleaning and are compared in the candidate scoring instead:
    // the old regex stripped them, which let a live/instrumental upload match
    // the studio entry with no version penalty at all.
    private static readonly Regex UploadNoiseRegex = new(
        @"[\(\[][^\)\]]*?(?:official|soundtrack|ost|original\s+motion\s+picture|lyric|video|visuali[sz]er|audio|4k|8k|\bhd\b|\bhq\b|\bmv\b|music\s+video|color\s+coded|topic)[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionQualifierRegex = new(
        @"\b(?:live|acoustic|unplugged|instrumental|karaoke|demo|remix|re-?\s?mix|sped\s*up|slowed(?:\s*\+\s*reverb)?|nightcore|radio\s+edit|tv\s+size|extended|orchestral|8d)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionQualifierBracketRegex = new(
        @"[\(\[][^\)\]]*?\b(?:live|acoustic|unplugged|instrumental|karaoke|demo|remix|re-?\s?mix|sped\s*up|slowed|nightcore|radio\s+edit|tv\s+size|extended|orchestral|8d)\b[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionQualifierSuffixRegex = new(
        @"\s*[-–—]\s*(?:live|acoustic|unplugged|instrumental|karaoke|demo|remix|re-?\s?mix|sped\s*up|slowed|nightcore|radio\s+edit|tv\s+size|extended|orchestral|8d)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Mastering/label tails do not change the recording, so they are safe to
    // drop before a lookup: "Bohemian Rhapsody - Remastered 2011".
    private static readonly Regex DescriptorSuffixRegex = new(
        @"\s+-\s+(?:remaster(?:ed)?(?:\s*\d{4})?|\d{4}\s+remaster(?:ed)?|mono|stereo|explicit|clean)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The tail on its own, for deciding whether "A - B" is a descriptor after
    // the song name ("Song - Remastered 2011") or the song after an artist
    // ("Rick Astley - Never Gonna Give You Up").
    private static readonly Regex VersionLabelRegex = new(
        @"^(?:remaster(?:ed)?(?:\s*\d{4})?|\d{4}\s+remaster(?:ed)?|mono|stereo|explicit|clean)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static LyricsClient()
    {
        SharedClient.DefaultRequestHeaders.Add("User-Agent", "Musicthingy/1.5 (https://github.com/avinaces/MusicthingyV2)");
    }

    /// <summary>
    /// Master fetching algorithm combining YT Music metadata inference, LRCLIB 4-tier fallback, and YT Native fallback.
    /// </summary>
    /// <param name="duration">
    /// Length of the recording, when known. LRCLIB filters exact matches to a
    /// ±2 s window, so passing it stops a remaster/edit from answering for the
    /// track; it also scores search candidates. Null when unknown.
    /// </param>
    /// <param name="cancellationToken">Cancels the search (e.g. the user started another one).</param>
    public static async Task<string?> FetchLyricsAsync(
        string url,
        string title,
        string artist,
        bool preferTimed = true,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(OverallTimeout);

        try
        {
            return await FetchLyricsCoreAsync(url, title, artist, preferTimed, duration, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own budget expired. Report it as "no lyrics found" instead of
            // an error - the user only needs to know the search is over.
            Log($"overall budget of {OverallTimeout.TotalSeconds:F0}s expired");
            return null;
        }
    }

    private static async Task<string?> FetchLyricsCoreAsync(
        string url, string title, string artist, bool preferTimed, TimeSpan? duration, CancellationToken ct)
    {
        url ??= string.Empty;
        title ??= string.Empty;
        artist ??= string.Empty;

        string videoId = "";
        string? album = null;

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

                            // The video length is the only duration we get for
                            // a stream we have not started yet; the caller's
                            // value (from a local file) wins when it has one.
                            if ((duration is not { } knownDuration || knownDuration <= TimeSpan.Zero) &&
                                details.TryGetProperty("lengthSeconds", out var lengthEl) &&
                                lengthEl.ValueKind == JsonValueKind.String &&
                                double.TryParse(lengthEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out double seconds) &&
                                seconds > 0)
                            {
                                duration = TimeSpan.FromSeconds(seconds);
                            }

                            // Auto-generated "Artist - Topic" uploads carry
                            // structured credits in the description:
                            //   Provided to YouTube by <label>
                            //   <Song> · <Artist>
                            //   <Album>
                            //   ℗ <year> <label>
                            // That is canonical metadata, so it beats the
                            // video title (which is full of "(Official Video)"
                            // noise) and the "Artist - Topic" channel name.
                            if (details.TryGetProperty("shortDescription", out var descriptionEl) &&
                                descriptionEl.ValueKind == JsonValueKind.String &&
                                ParseTopicDescription(descriptionEl.GetString()) is { } topic)
                            {
                                title = topic.Title;
                                artist = topic.Artist;
                                album = topic.Album;
                            }
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

        // Strip upload noise first, then split "Artist - Song". The split also
        // runs when the artist is unknown (YouTube uploads usually say
        // "Artist - Song"), which is what makes the LRCLIB artist-dependent
        // tiers usable for playlist tracks that only carry a song title.
        var (cleanedTitle, effectiveArtist) = CleanTrackMetadata(title, artist);
        string baseTitle = StripVersionQualifiers(cleanedTitle);
        string[] versionTags = ExtractVersionTags(cleanedTitle);

        Log($"lrclib query: title='{cleanedTitle}' base='{baseTitle}' artist='{effectiveArtist}' " +
            $"album='{album}' duration={(duration.HasValue ? $"{duration.Value.TotalSeconds:F0}s" : "?")}" +
            $"{(versionTags.Length > 0 ? $" tags=[{string.Join(',', versionTags)}]" : "")}");

        // 2. Try LRCLIB (4-Tier Fallback Strategy)
        try
        {
            string? lrcResult = await SearchLrcLibAsync(
                cleanedTitle, baseTitle, effectiveArtist, versionTags, duration, preferTimed, ct);
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

    private static async Task<string?> SearchLrcLibAsync(
        string cleanedTitle,
        string baseTitle,
        string artist,
        string[] versionTags,
        TimeSpan? duration,
        bool preferTimed,
        CancellationToken ct)
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
            // Tier 1 keeps recording qualifiers ("Song (Live)") when the title
            // has them: that entry is the right one for that recording.
            var tier1 = await RequestExactTierAsync(cleanedTitle, artist, duration, targetProp, fallbackProp, ct);
            if (tier1.Preferred != null) return tier1.Preferred;
            fallbackLyrics ??= tier1.Fallback;

            if (!string.Equals(baseTitle, cleanedTitle, StringComparison.OrdinalIgnoreCase))
            {
                var tier2 = await RequestExactTierAsync(baseTitle, artist, duration, targetProp, fallbackProp, ct);
                if (tier2.Preferred != null) return tier2.Preferred;
                fallbackLyrics ??= tier2.Fallback;
            }
        }

        // TIER 3: fuzzy search, same artist required. Skipped when the artist
        // is unknown: with an empty artist this is the EXACT same query as
        // Tier 4 (ArtistMatches returns true for ""), so it could only add a
        // duplicate round-trip.
        if (!string.IsNullOrWhiteSpace(artist))
        {
            var tier3 = await RequestLrcLibSearchAsync(
                $"{baseTitle} {artist}", baseTitle, artist, versionTags, duration,
                targetProp, fallbackProp, requireArtistMatch: true, ct);
            if (tier3.Preferred != null) return tier3.Preferred;
            fallbackLyrics ??= tier3.Fallback;
        }

        // TIER 4: fuzzy search, cleaned title only - recall over precision, the
        // scoring step still picks the best candidate.
        var tier4 = await RequestLrcLibSearchAsync(
            baseTitle, baseTitle, artist, versionTags, duration,
            targetProp, fallbackProp, requireArtistMatch: false, ct);
        if (tier4.Preferred != null) return tier4.Preferred;
        fallbackLyrics ??= tier4.Fallback;

        return fallbackLyrics;
    }

    /// <summary>
    /// Exact lookup with the duration filter, retried without it when nothing
    /// matched. A music video's length includes its intro/outro, so the strict
    /// ±2 s window can reject the studio entry that is still the right song.
    /// </summary>
    private static async Task<(string? Preferred, string? Fallback)> RequestExactTierAsync(
        string trackName, string artist, TimeSpan? duration, string targetProp, string fallbackProp, CancellationToken ct)
    {
        var strict = await RequestExactLrcLibAsync(trackName, artist, duration, targetProp, fallbackProp, ct);
        if (strict.Preferred != null || duration == null)
            return strict;

        var lenient = await RequestExactLrcLibAsync(trackName, artist, null, targetProp, fallbackProp, ct);
        return (lenient.Preferred, strict.Fallback ?? lenient.Fallback);
    }

    private static async Task<(string? Preferred, string? Fallback)> RequestExactLrcLibAsync(
        string trackName, string artist, TimeSpan? duration, string targetProp, string fallbackProp, CancellationToken ct)
    {
        try
        {
            string requestUrl =
                $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(trackName)}&artist_name={Uri.EscapeDataString(artist)}";
            string? seconds = DurationParameter(duration);
            if (seconds != null)
                requestUrl += $"&duration={seconds}";

            using var response = await SharedClient.GetAsync(requestUrl, ct);

            if (!response.IsSuccessStatusCode)
            {
                // 404 is the normal "no exact match"; 429 and 5xx are worth a log.
                if ((int)response.StatusCode != 404)
                    Log($"lrclib get '{trackName}' {(seconds != null ? $"({seconds}s) " : "")}-> HTTP {(int)response.StatusCode}");
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

    /// <summary>Durations are whole seconds, invariant, or null when unknown.</summary>
    private static string? DurationParameter(TimeSpan? duration)
    {
        if (duration is not { } value || value <= TimeSpan.Zero)
            return null;

        return ((int)Math.Round(value.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    }

    private static async Task<(string? Preferred, string? Fallback)> RequestLrcLibSearchAsync(
        string query,
        string baseTitle,
        string artist,
        string[] versionTags,
        TimeSpan? duration,
        string targetProp,
        string fallbackProp,
        bool requireArtistMatch,
        CancellationToken ct)
    {
        try
        {
            using var response = await SharedClient.GetAsync(
                $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}", ct);

            if (!response.IsSuccessStatusCode)
            {
                Log($"lrclib search '{query}' -> HTTP {(int)response.StatusCode}");
                return (null, null);
            }

            return ParseLrclibSearch(
                await response.Content.ReadAsStringAsync(ct),
                baseTitle, artist, versionTags, duration, targetProp, fallbackProp, requireArtistMatch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"lrclib search '{query}' failed: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Separates upload noise from the actual "artist + song", returning both so
    /// the artist-dependent LRCLIB tiers work even when the caller only has a
    /// video title.
    /// </summary>
    private static (string Title, string Artist) CleanTrackMetadata(string rawTitle, string artist)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
            return ("", artist?.Trim() ?? "");

        string cleaned = UploadNoiseRegex.Replace(rawTitle, "");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();

        string effectiveArtist = artist?.Trim() ?? "";

        // "Artist - Song", the usual YouTube layout. With a known artist the
        // prefix is dropped only when it IS that artist (the old code always
        // took the last dash segment, which turned "Bohemian Rhapsody -
        // Remastered 2011" into "Remastered 2011"). With no artist, the left
        // side becomes the artist when it reads like a name and the right side
        // is not just a version label.
        int dash = cleaned.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            string left = cleaned[..dash].Trim();
            string right = cleaned[(dash + 3)..].Trim();

            if (effectiveArtist.Length > 0 && left.Equals(effectiveArtist, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = right;
            }
            else if (effectiveArtist.Length == 0 && right.Length > 0 &&
                     !IsVersionLabel(right) && LooksLikeArtistName(left))
            {
                effectiveArtist = left;
                cleaned = right;
            }
        }

        // Strip a mastering tail ("- Remastered 2011") from what is left,
        // keeping the song name.
        cleaned = DescriptorSuffixRegex.Replace(cleaned, "");

        cleaned = Regex.Replace(cleaned, @"^(track\s*\d+|\d+)[\s\.\-\:]+", "", RegexOptions.IgnoreCase);
        if (cleaned.Contains(" | ")) cleaned = cleaned.Split(" | ").First();

        return (cleaned.Trim(), effectiveArtist);
    }

    /// <summary>
    /// True for a dash tail naming a version ("Live at Wembley", "Remastered 2011").
    /// </summary>
    private static bool IsVersionLabel(string text) =>
        VersionQualifierRegex.IsMatch(text) || VersionLabelRegex.IsMatch(text);

    /// <summary>
    /// Credits of an auto-generated "Artist - Topic" upload, read from the
    /// description YouTube builds for it:
    /// <code>
    /// Provided to YouTube by Universal Music Group
    ///
    /// Yesterday · The Beatles
    ///
    /// 1 (Remastered)
    ///
    /// ℗ 2015 Calderstone Productions Limited
    /// </code>
    /// Returns null for a normal creator-written description, so nothing is
    /// invented when the upload is not an art track.
    /// </summary>
    private static (string Title, string Artist, string? Album)? ParseTopicDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;

        var lines = description
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        if (lines.Length < 2 ||
            !lines[0].StartsWith("Provided to YouTube by", StringComparison.OrdinalIgnoreCase))
            return null;

        // The credits line is "<Song> · <Artist>"; the album follows on the
        // next line and the label/licence is after that.
        for (int i = 1; i < lines.Length; i++)
        {
            const string Separator = " · ";
            int split = lines[i].IndexOf(Separator, StringComparison.Ordinal);
            if (split <= 0)
                continue;

            string topicTitle = lines[i][..split].Trim();
            string topicArtist = lines[i][(split + Separator.Length)..].Trim();
            if (topicTitle.Length == 0 || topicArtist.Length == 0)
                continue;

            string? topicAlbum = null;
            if (i + 1 < lines.Length)
            {
                string candidate = lines[i + 1];
                if (!candidate.StartsWith('℗') &&
                    !candidate.StartsWith("Released on", StringComparison.OrdinalIgnoreCase) &&
                    !candidate.StartsWith("Auto-generated", StringComparison.OrdinalIgnoreCase))
                {
                    topicAlbum = candidate;
                }
            }

            return (topicTitle, topicArtist, topicAlbum);
        }

        return null;
    }

    /// <summary>
    /// A dash head that reads like an artist name rather than the first half of
    /// a song title: short, no version qualifier, not a bare number.
    /// </summary>
    private static bool LooksLikeArtistName(string text)
    {
        if (text.Length is < 2 or > 60) return false;
        if (VersionQualifierRegex.IsMatch(text)) return false;

        return !Regex.IsMatch(text, @"^\d+$");
    }

    /// <summary>
    /// The cleaned title with recording qualifiers removed ("Song (Live)" ->
    /// "Song"). Used as the LATER, less precise lookup: the tagged title is
    /// tried first, so a live/remix entry still wins when one exists.
    /// </summary>
    private static string StripVersionQualifiers(string cleanedTitle)
    {
        string stripped = VersionQualifierBracketRegex.Replace(cleanedTitle, "");
        stripped = VersionQualifierSuffixRegex.Replace(stripped, "");
        stripped = Regex.Replace(stripped, @"\s{2,}", " ").Trim();

        return stripped.Length > 0 ? stripped : cleanedTitle;
    }

    /// <summary>
    /// Normalized recording qualifiers ("live", "remix", "instrumental" ...).
    /// </summary>
    private static string[] ExtractVersionTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        return VersionQualifierRegex.Matches(text)
            .Select(match => NormalizeTag(match.Value))
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeTag(string tag) =>
        Regex.Replace(tag.ToLowerInvariant(), @"[^a-z0-9]", "");

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

    /// <summary>
    /// Picks the best candidate instead of the first title match, and returns
    /// BOTH lyrics kinds so a plain-only entry found by search is not lost when
    /// synced lyrics were preferred (previously only the exact tiers kept it).
    /// </summary>
    private static (string? Preferred, string? Fallback) ParseLrclibSearch(
        string jsonStr,
        string baseTitle,
        string artist,
        string[] versionTags,
        TimeSpan? duration,
        string targetProp,
        string fallbackProp,
        bool requireArtistMatch)
    {
        using var doc = JsonDocument.Parse(jsonStr);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return (null, null);

        (string? Preferred, string? Fallback) best = (null, null);
        int bestScore = 0;

        foreach (var result in doc.RootElement.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object)
                continue;

            string? preferred = ReadLyricsProperty(result, targetProp);
            string? fallback = ReadLyricsProperty(result, fallbackProp);
            if (preferred == null && fallback == null)
                continue;

            string foundTitle = ReadString(result, "trackName");
            if (!TitleMatches(foundTitle, baseTitle))
                continue;

            string foundArtist = ReadString(result, "artistName");
            bool artistMatches = ArtistMatches(foundArtist, artist);
            if (requireArtistMatch && !artistMatches)
                continue;

            int score = ScoreCandidate(result, foundTitle, baseTitle, artistMatches,
                artistIsKnown: !string.IsNullOrWhiteSpace(artist), versionTags, duration);
            if (score <= bestScore)
                continue;

            bestScore = score;
            best = (preferred, fallback);
        }

        return best;
    }

    /// <summary>
    /// Weighted confidence for one search candidate: title, artist, recording
    /// qualifier parity, instrumental flag, then duration proximity.
    /// </summary>
    /// <remarks>
    /// Duration is a strong signal but deliberately NOT a hard reject beyond a
    /// few seconds: a music video's length includes its intro, so the studio
    /// entry is usually 10-30 s off and still the only correct lyrics.
    /// </remarks>
    private static int ScoreCandidate(
        JsonElement result,
        string foundTitle,
        string baseTitle,
        bool artistMatches,
        bool artistIsKnown,
        string[] versionTags,
        TimeSpan? duration)
    {
        int score = string.Equals(NormalizeForMatch(foundTitle), NormalizeForMatch(baseTitle), StringComparison.Ordinal)
            ? 100
            : 60; // word-boundary substring match, already checked by TitleMatches

        if (artistIsKnown)
            score += artistMatches ? 40 : -60;

        // Recording qualifier parity: a live/remix/instrumental entry must not
        // pass for the studio recording, and vice versa.
        string[] candidateTags = ExtractVersionTags(foundTitle);
        foreach (string tag in versionTags)
        {
            if (!candidateTags.Contains(tag, StringComparer.Ordinal))
                score -= 35;
        }

        foreach (string tag in candidateTags)
        {
            if (!versionTags.Contains(tag, StringComparer.Ordinal))
                score -= 20;
        }

        bool candidateInstrumental =
            result.TryGetProperty("instrumental", out JsonElement instrumentalEl) &&
            instrumentalEl.ValueKind == JsonValueKind.True;
        if (candidateInstrumental && !versionTags.Contains("instrumental", StringComparer.Ordinal))
            score -= 60;

        if (duration is { } wanted && wanted > TimeSpan.Zero &&
            result.TryGetProperty("duration", out JsonElement durationEl) &&
            durationEl.ValueKind == JsonValueKind.Number &&
            durationEl.TryGetDouble(out double candidateSeconds))
        {
            double delta = Math.Abs(candidateSeconds - wanted.TotalSeconds);
            if (delta <= 2) score += 25;
            else if (delta <= 5) score += 10;
            else if (delta > 12) score -= 25;
        }

        return score;
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

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
