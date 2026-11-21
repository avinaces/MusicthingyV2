using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Musicthingy.Models;
using Musicthingy.Services;

namespace Musicthingy.Services.YouTube;

// ────────────────────────────────────────────────────────────────────────────
// YouTubeAccountPlaylistsService — lists the playlists of the LOGGED-IN
// YouTube account and imports one as a Musicthingy Playlist.
//
// Endpoint research (all three sources agree):
//   • ytmusicapi  (external/ytmusicapi, mixins/library.py):  get_library_playlists()
//       POSTs {"browseId": "FEmusic_liked_playlists"} to /youtubei/v1/browse with
//       the WEB_REMIX (YouTube Music web) client, cookie + SAPISIDHASH auth, and
//       parses gridRenderer items (items[0] is the "New playlist" tile, skipped).
//   • youtubei.js (LuanRT/YouTube.js, core/clients/Music.ts): library via browse
//       FEmusic_library_landing / FEmusic_liked_playlists, WEB_REMIX equivalent
//       client, response parsed as SectionList → Grid/MusicShelf.
//   • kaset API-discovery doc (github.com/sozercan/kaset): FEmusic_liked_playlists
//       = "Library Playlists", auth required, returns gridRenderer of
//       musicTwoRowItemRenderer playlist cards; VL{playlistId} = "Playlist Detail"
//       (public, needs no auth) returning musicPlaylistShelfRenderer rows;
//       VLLM is the special case for Liked Songs. Playlist cards navigate via
//       navigationEndpoint.browseEndpoint.browseId (already VL-prefixed).
//
// The app already speaks this exact dialect for /player: per-request
// Cookie + SAPISIDHASH headers (BuildPlayerRequestAsync) on the cookie-free
// InnerTubeHandler. This service reuses the same auth + handler and adds the
// WEB_REMIX browse identity (music.youtube.com origin, like ytmusicapi).
//
// Continuations: playlist contents pages come back 100-per-page (ytmusicapi
// get_continuations): the last item of each page is a continuationItemRenderer
// whose token we send back as {"continuation": token}; the next page arrives in
// onResponseReceivedActions[0].appendContinuationItemsAction.continuationItems.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>A YouTube playlist as shown in the account's Library Playlists grid.</summary>
public sealed record YouTubeAccountPlaylist(
    string PlaylistId,
    string Title,
    string? ThumbnailUrl,
    string? TrackCountText,
    bool Owned);

/// <summary>A playlist plus everything needed to build the app-side Playlist: resolved contents + metadata.</summary>
public sealed record YouTubePlaylistImport(
    YouTubeAccountPlaylist Playlist,
    IReadOnlyList<YouTubePlaylistVideo> Videos,
    string? PlaylistThumbnailUrl);

public sealed class YouTubeAccountPlaylistsService
{
    private const string MusicOrigin = "https://music.youtube.com";

    // WEB_REMIX — the YouTube Music web client, exactly like ytmusicapi's
    // initialize_context(). The /browse key param is the same key the resolver
    // already uses for /player (InnerTubeApiKey).
    private const string ClientName = "WEB_REMIX";
    private const string ClientVersion = "1.20260819.01.00";

    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // 3 pages ≈ 300 playlists; enough for any real account, bounded traffic.
    private const int MaxPlaylistPages = 3;
    private const int MaxVideoPages = 200; // ≈ 20k videos; whole playlists in practice

    public YouTubeAccountPlaylistsService(IHttpClientFactory? httpFactory = null)
    {
        if (httpFactory != null)
            _http = httpFactory.CreateClient("youtube");
        else
            _http = new HttpClient(YouTubeResolverService.InnerTubeHandler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
    }

    public static bool IsAvailable => YouTubeResolverService.HasCookies;

    // ─────────────────────────────────────────────────────────────────────
    // Library playlist list (FEmusic_liked_playlists)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the signed-in account's playlist-like lists: their own playlists,
    /// playlists other accounts shared/saved, plus the two special account
    /// lists (Liked Songs, Watch Later). Returns null on auth trouble (caller
    /// shows the sign-in hint), an empty list only when the account truly has
    /// nothing.
    /// </summary>
    public async Task<IReadOnlyList<YouTubeAccountPlaylist>?> GetAccountPlaylistsAsync(CancellationToken ct = default)
    {
        if (!YouTubeResolverService.HasCookies)
        {
            Trace("[YTPL] no account cookies — treating as signed out");
            return null;
        }

        try
        {
            // The library response is NOT a single flat grid: it is a
            // singleColumnBrowseResultsRenderer whose tab holds a
            // sectionListRenderer with one section per playlist group the
            // account has (e.g. "Your playlists" grid + "Saved playlists"
            // grid). The old single FindFirstArray(root,"items") walk only
            // ever saw the FIRST group and dropped the rest — one reason a
            // populated account could come back "empty".
            using JsonDocument doc = await BrowseAsync("FEmusic_liked_playlists", null, ct);
            JsonElement root = doc.RootElement;

            List<YouTubeAccountPlaylist> playlists = ParseAllLibrarySections(root);
            Trace($"[YTPL] library sections parsed: {playlists.Count} playlist entries");

            // gridContinuation paging (ytmusicapi get_continuations): a grid
            // with more pages ends with a continuationItemRenderer token.
            // Deduplicate as we go: created + saved shelves can overlap when
            // YouTube echoes a playlist into both groups.
            string? token = FindContinuationToken(root);
            int pages = 1;
            while (token != null && playlists.Count < 300 && pages < MaxPlaylistPages)
            {
                using JsonDocument next = await BrowseAsync(null, token, ct);
                JsonElement items = FindContinuationItems(next.RootElement);
                int before = playlists.Count;
                playlists.AddRange(ParsePlaylistGrid(items));
                Trace($"[YTPL] continuation page {pages}: +{playlists.Count - before} entries");
                token = FindContinuationToken(items);
                pages++;
            }

            // Not signed in / cookies rejected: the response is a bare
            // messageRenderer ("Sign in to listen...") with no grids at all.
            if (playlists.Count == 0 && HasSignInMessage(root))
            {
                Trace("[YTPL] response is a sign-in messageRenderer — cookies not accepted");
                return null;
            }

            // Special account lists (ytmusicapi get_liked_songs browses "LM";
            // watch later browses "VLWL" — verified: bare "WL" → HTTP 400).
            // They only exist as tiles when non-empty, so probe each cheaply:
            // a 200 with a shelf means the list has contents worth importing.
            List<YouTubeAccountPlaylist> specials = await ProbeSpecialListsAsync(ct);
            foreach (YouTubeAccountPlaylist special in specials)
            {
                if (!playlists.Any(p => p.PlaylistId == special.PlaylistId))
                {
                    playlists.Insert(0, special);
                    Trace($"[YTPL] added special list '{special.Title}' ({special.PlaylistId})");
                }
            }

            return playlists;
        }
        catch (Exception ex)
        {
            Trace($"[YTPL] library listing failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Walks every section of the library tab and parses every playlist card it
    /// finds, instead of stopping at the first "items" array. Handles BOTH
    /// response eras YouTube is serving for the signed-in library page:
    ///   Renderer era: sectionListRenderer → gridRenderer.items →
    ///                 musicTwoRowItemRenderer (browseId VL…) tiles
    ///   ViewModel era (new A/B): sectionListViewModel → gridViewModel /
    ///                 musicShelfViewModel → lockupViewModel (contentId VL…,
    ///                 LOCKUP_CONTENT_TYPE_PLAYLIST) or other *ViewModel cards
    /// The walk is structural: it recurses the whole response collecting every
    /// node that *is* a playlist card, so the exact wrapper names matter less
    /// than the card-level fields (browse id + title + thumbnail).
    /// </summary>
    private static List<YouTubeAccountPlaylist> ParseAllLibrarySections(JsonElement root)
    {
        List<YouTubeAccountPlaylist> result = new();

        // Structural card scan — works for both eras and any wrapper shape.
        CollectPlaylistCards(root, result, depth: 0);

        if (result.Count == 0)
        {
            Trace("[YTPL] no playlist cards found in library response — shape dump follows");
            DumpShapeNames(root);
            return result;
        }

        Trace($"[YTPL] library cards parsed: {result.Count} playlist entries");

        // Deduplicate by playlist id (first occurrence wins; owned beats saved
        // because the "your playlists" shelf comes first).
        List<YouTubeAccountPlaylist> deduped = new();
        HashSet<string> seen = new();
        foreach (YouTubeAccountPlaylist p in result)
        {
            if (seen.Add(p.PlaylistId))
                deduped.Add(p);
        }
        return deduped;
    }

    /// <summary>
    /// Recursively collects playlist cards. A "card" is any object node that
    /// carries a playlist-like browse id (VL…, or the specials LM/OL…/SE):
    ///   • musicTwoRowItemRenderer and friends (Renderer era)
    ///   • lockupViewModel with LOCKUP_CONTENT_TYPE_PLAYLIST/PODCAST
    ///   • any *ViewModel node whose contentId/overlay route is a playlist
    /// Non-card nodes are descended into (grids, shelves, sections, tabs).
    /// </summary>
    private static void CollectPlaylistCards(JsonElement node, List<YouTubeAccountPlaylist> result, int depth)
    {
        if (depth > 12 || node.ValueKind != JsonValueKind.Object)
            return;

        // Renderer-era grid tile
        if (node.TryGetProperty("musicTwoRowItemRenderer", out JsonElement twoRow) &&
            TryParseCard(twoRow) is YouTubeAccountPlaylist p1)
        {
            result.Add(p1);
            return; // card parsed; do not descend further
        }

        // Renderer-era list row (some shelves use these instead of grids)
        if (node.TryGetProperty("musicResponsiveListItemRenderer", out JsonElement listRow) &&
            TryParseCard(listRow) is YouTubeAccountPlaylist p2 &&
            IsPlaylistCard(listRow))
        {
            result.Add(p2);
            return;
        }

        // ViewModel-era lockup (grid/section entries)
        if (node.TryGetProperty("lockupViewModel", out JsonElement lockup) &&
            TryParseLockup(lockup) is YouTubeAccountPlaylist p3)
        {
            result.Add(p3);
            return;
        }

        // ViewModel-era list row (musicResponsiveListItemViewModel carries the
        // playlist id in its contentId/firstSegment)
        if (node.TryGetProperty("musicResponsiveListItemViewModel", out JsonElement mrlvm) &&
            TryParseListItemViewModel(mrlvm) is YouTubeAccountPlaylist p4)
        {
            result.Add(p4);
            return;
        }

        // Playlist page-preview card some accounts get (playlistViewModel /
        // other *ViewModel siblings): try generically on any *ViewModel node.
        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Name.EndsWith("ViewModel", StringComparison.Ordinal) &&
                prop.Value.ValueKind == JsonValueKind.Object &&
                TryParseLockup(prop.Value) is YouTubeAccountPlaylist p5 &&
                p5.PlaylistId != null)
            {
                result.Add(p5);
                return; // first ViewModel card wins at this level
            }
        }

        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                Descend(prop.Value, result, depth);
        }
    }

    /// <summary>Descends into arrays (each element separately) and objects.</summary>
    private static void Descend(JsonElement value, List<YouTubeAccountPlaylist> result, int depth)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                CollectPlaylistCards(item, result, depth + 1);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            CollectPlaylistCards(value, result, depth + 1);
        }
    }

    /// <summary>
    /// One card → playlist entry, from any card-shaped node. Works off the
    /// card's own fields, not its wrapper: browse id (navigationEndpoint →
    /// browseEndpoint.browseId, or contentId), title, thumbnail, subtitle.
    /// </summary>
    private static YouTubeAccountPlaylist? TryParseCard(JsonElement card)
    {
        string? browseId = ExtractBrowseId(card) ?? ExtractString(card, "contentId");
        if (browseId == null || !IsPlaylistBrowseId(browseId))
            return null;

        string title = ExtractTitle(card) ?? ExtractLockupTitle(card) ?? "Playlist";
        string? thumb = ExtractThumbnail(card) ?? ExtractLockupThumbnail(card);
        string? countText = ExtractSubtitle(card) ?? ExtractLockupMetadataText(card);
        bool owned = ExtractOwnedFlag(card);

        return new YouTubeAccountPlaylist(
            NormalizePlaylistId(browseId), title, thumb, countText, owned);
    }

    /// <summary>lockupViewModel → playlist (yt-dlp _extract_lockup_view_model shape).</summary>
    private static YouTubeAccountPlaylist? TryParseLockup(JsonElement lockup)
    {
        string? contentId = ExtractString(lockup, "contentId");
        string? contentType = ExtractString(lockup, "contentType");
        if (contentId == null)
            return null;

        // Only playlist-ish lockups; video lockups are not library playlists.
        if (contentType != null && !contentType.Contains("PLAYLIST") && !contentType.Contains("PODCAST"))
            return null;

        if (!IsPlaylistBrowseId(contentId))
            return null;

        string title = ExtractLockupTitle(lockup) ?? "Playlist";
        string? thumb = ExtractLockupThumbnail(lockup);
        string? countText = ExtractLockupMetadataText(lockup);

        return new YouTubeAccountPlaylist(
            NormalizePlaylistId(contentId), title, thumb, countText, Owned: true);
    }

    /// <summary>musicResponsiveListItemViewModel → playlist (new list rows).</summary>
    private static YouTubeAccountPlaylist? TryParseListItemViewModel(JsonElement row)
    {
        // The id lives in contentId or inside overlay/onTap routing; text in
        // the first segment of the primary text row.
        string? contentId = ExtractString(row, "contentId");
        if (contentId == null)
        {
            contentId = WalkForBrowseId(row, depth: 0);
        }
        if (contentId == null || !IsPlaylistBrowseId(contentId))
            return null;

        string title = ExtractViewModelText(row, "primaryText") ?? "Playlist";
        string? thumb = ExtractLockupThumbnail(row) ?? ExtractThumbnail(row);
        string? countText = ExtractViewModelText(row, "secondaryText");

        return new YouTubeAccountPlaylist(
            NormalizePlaylistId(contentId), title, thumb, countText, Owned: true);
    }

    /// <summary>
    /// Playlist-like browse id: VL… (playlist), plus the account specials
    /// (LM Liked Songs, WL/SE saved lists, OL… podcasts, RD radio mixes stay
    /// out — they are not importable playlists).
    /// </summary>
    private static bool IsPlaylistBrowseId(string id)
    {
        return id.StartsWith("VL", StringComparison.Ordinal)
            || id is "LM" or "WL" or "SE"
            || id.StartsWith("OLA", StringComparison.Ordinal); // album
    }

    /// <summary>
    /// musicResponsiveListItemRenderer rows serve both videos and playlists on
    /// mixed shelves; only treat them as playlist cards when they actually
    /// carry a browseEndpoint (playlists navigate to a page; videos don't).
    /// </summary>
    private static bool IsPlaylistCard(JsonElement row)
    {
        return row.TryGetProperty("navigationEndpoint", out JsonElement nav) &&
               nav.TryGetProperty("browseEndpoint", out _);
    }

    /// <summary>True when the library response is just a "Sign in" messageRenderer (anonymous/invalid cookies).</summary>
    private static bool HasSignInMessage(JsonElement root)
    {
        return WalkForKey(root, "messageRenderer", depth: 0);
    }

    /// <summary>
    /// Probes the two special account lists. Both are browsed VL-prefixed
    /// (ytmusicapi get_liked_songs → get_playlist("LM") → browseId "VLLM";
    /// Watch Later is "VLWL"). A response with any track row — Renderer or
    /// ViewModel era — means the list is non-empty and importable.
    /// </summary>
    private async Task<List<YouTubeAccountPlaylist>> ProbeSpecialListsAsync(CancellationToken ct)
    {
        List<YouTubeAccountPlaylist> result = new();

        // (id, title, browse id) — VL-prefixed, exactly like every other
        // playlist page browse (bare "LM" is HTTP 400; verified live).
        (string id, string title, string browse)[] specials =
        {
            ("LM", "Liked Songs", "VLLM"),
            ("WL", "Watch Later", "VLWL"),
        };

        foreach ((string id, string title, string browse) in specials)
        {
            try
            {
                using JsonDocument doc = await BrowseAsync(browse, null, ct);
                JsonElement root = doc.RootElement;

                // Non-empty when the page carries any track row — same
                // structural walk the import path uses, so the verdict
                // matches what importing would actually yield.
                bool hasItems = HasTrackRows(root);
                if (!hasItems)
                {
                    // …or the account title (an empty list has neither).
                    hasItems = !string.IsNullOrEmpty(ParsePlaylistHeaderTitle(root));
                }

                if (hasItems)
                {
                    result.Add(new YouTubeAccountPlaylist(id, title, null, null, Owned: true));
                    Trace($"[YTPL] special '{title}' ({browse}) non-empty — offering import");
                }
                else
                {
                    Trace($"[YTPL] special '{title}' ({browse}) empty — skipped");
                }
            }
            catch (Exception ex)
            {
                // Specials are best-effort only; a failed probe must not
                // sink the whole listing.
                Trace($"[YTPL] special '{title}' ({browse}) probe failed: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// True when the page contains at least one track row in ANY era's shape:
    /// contents/items arrays holding video renderers, or the new-format
    /// musicResponsiveListItemViewModel / lockupViewModel video cards.
    /// </summary>
    private static bool HasTrackRows(JsonElement root)
    {
        return WalkForVideoRow(root, depth: 0);
    }

    private static bool WalkForVideoRow(JsonElement node, int depth)
    {
        if (depth > 12 || node.ValueKind != JsonValueKind.Object)
            return false;

        // Renderer-era video rows
        if (node.TryGetProperty("musicResponsiveListItemRenderer", out JsonElement row) &&
            HasVideoId(row))
            return true;
        if (node.TryGetProperty("playlistPanelVideoRenderer", out JsonElement panel) &&
            HasVideoId(panel))
            return true;
        if (node.TryGetProperty("musicTwoRowItemRenderer", out JsonElement twoRow) &&
            HasVideoId(twoRow))
            return true;

        // ViewModel-era video rows
        if (node.TryGetProperty("musicResponsiveListItemViewModel", out JsonElement mrlvm) &&
            HasVideoId(mrlvm))
            return true;
        if (node.TryGetProperty("lockupViewModel", out JsonElement lockup) &&
            lockup.TryGetProperty("contentType", out JsonElement ct) &&
            ct.ValueKind == JsonValueKind.String &&
            ct.GetString() == "LOCKUP_CONTENT_TYPE_VIDEO" &&
            lockup.TryGetProperty("contentId", out JsonElement vid) &&
            vid.ValueKind == JsonValueKind.String)
            return true;

        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array &&
                WalkForVideoRow(prop.Value, depth + 1))
                return true;
        }
        return false;
    }

    /// <summary>Diagnostic: lists Renderer AND ViewModel key names present in the response (logged when parsing found nothing).</summary>
    private static void DumpShapeNames(JsonElement root)
    {
        List<string> keys = new();
        CollectShapeNames(root, keys, depth: 0);
        Trace("[YTPL] shape keys: " + (keys.Count > 0 ? string.Join(", ", keys.Take(30)) : "(none)"));
    }

    private static void CollectShapeNames(JsonElement node, List<string> keys, int depth)
    {
        if (depth > 8 || node.ValueKind != JsonValueKind.Object)
            return;

        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Name.EndsWith("Renderer", StringComparison.Ordinal) ||
                prop.Name.EndsWith("ViewModel", StringComparison.Ordinal))
                keys.Add(prop.Name);
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                CollectShapeNames(prop.Value, keys, depth + 1);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Playlist contents + metadata (VL{playlistId})
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches a playlist's full contents (all pages) and its metadata (title,
    /// thumbnail, track count). Works for both the user's own playlists and
    /// any public playlist id (VL browse is public — kaset: "🌐").
    /// </summary>
    public async Task<YouTubePlaylistImport?> GetPlaylistForImportAsync(
        string playlistId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            return null;

        try
        {
            string browseId = playlistId.StartsWith("VL", StringComparison.Ordinal)
                ? playlistId
                : "VL" + playlistId;

            using JsonDocument doc = await BrowseAsync(browseId, null, ct);

            string title = ParsePlaylistHeaderTitle(doc.RootElement) ?? "YouTube Playlist";
            string? headerThumb = ParsePlaylistHeaderThumbnail(doc.RootElement);

            // Collect track rows from the FIRST page. Structural walk: on the
            // classic page the rows live at contents.twoColumnBrowseResults…
            // .secondaryContents.sectionListRenderer.contents[0]
            // .musicPlaylistShelfRenderer.contents — the sectionList's own
            // "contents" is an array of SECTION wrappers, so a naive
            // FindFirstArray(root,"contents") grabs the wrappers and finds
            // nothing (reproduced live). The shelf's inner contents is what
            // holds rows; the ViewModel-era page delivers rows in
            // sections/items arrays — both handled here.
            List<YouTubePlaylistVideo> videos = new();
            JsonElement shelf = CollectPlaylistShelfRows(doc.RootElement, videos);

            string? token = FindContinuationTokenInShelf(shelf);
            int pages = 1;
            while (token != null && videos.Count < 20000 && pages < MaxVideoPages)
            {
                using JsonDocument next = await BrowseAsync(null, token, ct);
                List<YouTubePlaylistVideo> nextPage = new();
                JsonElement items = CollectPlaylistShelfRows(next.RootElement, nextPage);
                if (nextPage.Count == 0)
                    break;
                videos.AddRange(nextPage);
                token = FindContinuationTokenInShelf(items);
                pages++;
            }

            if (videos.Count == 0)
                return null;

            string? trackCountText = ParsePlaylistHeaderTrackCount(doc.RootElement);

            return new YouTubePlaylistImport(
                new YouTubeAccountPlaylist(
                    playlistId, title, headerThumb, trackCountText, Owned: true),
                videos,
                headerThumb ?? FirstVideoThumbnail(videos));
        }
        catch (Exception ex)
        {
            Trace($"[YTPaylists] playlist import failed for {playlistId}: {ex.Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Request plumbing
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends one /browse request. Pass browseId for a first page or continuation
    /// for a continuation page. Auth: per-request Cookie + SAPISIDHASH, exactly
    /// like the resolver's player requests (InnerTubeHandler has no cookie
    /// container by design — nothing leaks between requests).
    /// </summary>
    private async Task<JsonDocument> BrowseAsync(string? browseId, string? continuation, CancellationToken ct)
    {
        Dictionary<string, object> clientObj = new()
        {
            ["clientName"] = ClientName,
            ["clientVersion"] = ClientVersion,
            ["hl"] = "en",
            ["gl"] = "US"
        };

        Dictionary<string, object> payload = new()
        {
            ["context"] = new Dictionary<string, object>
            {
                ["client"] = clientObj,
                ["user"] = new Dictionary<string, object>()
            }
        };
        if (browseId != null)
            payload["browseId"] = browseId;
        if (continuation != null)
            payload["continuation"] = continuation;

        string apiUrl =
            $"{MusicOrigin}/youtubei/v1/browse?key={YouTubeResolverService.InnerTubeApiKey}&prettyPrint=false";

        using HttpRequestMessage request = new(HttpMethod.Post, apiUrl);
        // Same header set the resolver sends for the WEB family + TVHTML5.
        request.Headers.TryAddWithoutValidation("User-Agent", YouTubeResolverService.DefaultUserAgent);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Goog-Api-Format-Version", "1");
        request.Headers.Add("X-Origin", MusicOrigin);
        request.Headers.Add("Referer", MusicOrigin + "/");

        // Auth: Cookie + SAPISIDHASH (origin = music.youtube.com, the origin
        // the WEB_REMIX client actually browses on — ytmusicapi hashes the
        // same origin).
        string? cookie = YouTubeResolverService.Cookies;
        if (string.IsNullOrEmpty(cookie))
            throw new InvalidOperationException("YouTube account cookies are required for library browsing");

        request.Headers.Add("Cookie", cookie);
        string? sapisidHash = YouTubeResolverService.GenerateSapisidHash(cookie, MusicOrigin);
        if (sapisidHash != null)
            request.Headers.Add("Authorization", sapisidHash);

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        string json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"browse returned HTTP {(int)response.StatusCode} for {browseId ?? continuation}");

        return JsonDocument.Parse(json);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Response parsing helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a musicTwoRowItemRenderer grid (FEmusic_liked_playlists shape).
    /// items[0] on the first page is the "New playlist" tile — it has no
    /// browseId, so it drops out of the playlistId check naturally
    /// (ytmusicapi slices [1:] for the same reason).
    /// </summary>
    private static List<YouTubeAccountPlaylist> ParsePlaylistGrid(JsonElement grid)
    {
        List<YouTubeAccountPlaylist> result = new();

        JsonElement items = FindFirstArray(grid, "items");
        if (items.ValueKind != JsonValueKind.Array)
            return result;

        foreach (JsonElement item in items.EnumerateArray())
        {
            // The grid node itself can be the gridRenderer (first page) or a
            // bare items array (continuation page).
            JsonElement card = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("musicTwoRowItemRenderer", out JsonElement twoRow)
                ? twoRow
                : item;

            if (TryParseCard(card) is YouTubeAccountPlaylist playlist)
                result.Add(playlist);
            // New-format continuation pages deliver lockupViewModel entries
            // inside the same items arrays — TryParseCard already handles the
            // card-level fields via contentId when present.
        }

        return result;
    }

    // ── ViewModel-era field extractors ─────────────────────────────────────
    // yt-dlp's _extract_lockup_view_model documents the lockup shape:
    //   lockupViewModel { contentId, contentType,
    //     contentImage.thumbnailViewModel.image.sources[].url,
    //     metadata.lockupMetadataViewModel { title.content,
    //       metadata.contentMetadataViewModel.metadataRows[].metadataParts[].text } }
    // The music list rows (musicResponsiveListItemViewModel) carry text under
    // primaryText/secondaryText.content and route via onTap.innertubeCommand.

    private static string? ExtractString(JsonElement node, string name)
    {
        return node.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>lockupViewModel.metadata.lockupMetadataViewModel.title.content</summary>
    private static string? ExtractLockupTitle(JsonElement node)
    {
        if (node.TryGetProperty("metadata", out JsonElement metadata) &&
            metadata.TryGetProperty("lockupMetadataViewModel", out JsonElement lmvm) &&
            lmvm.TryGetProperty("title", out JsonElement title) &&
            title.TryGetProperty("content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.String)
            return content.GetString();

        // musicResponsiveListItemViewModel-style: text.content directly
        return ExtractString(node, "title");
    }

    /// <summary>lockupViewModel.contentImage…thumbnailViewModel.image.sources[].url (largest wins).</summary>
    private static string? ExtractLockupThumbnail(JsonElement node)
    {
        JsonElement image = default;
        bool found = false;

        if (node.TryGetProperty("contentImage", out JsonElement contentImage))
        {
            // playlists/podcasts: contentImage.collectionThumbnailViewModel.primaryThumbnail
            if (contentImage.TryGetProperty("collectionThumbnailViewModel", out JsonElement collection) &&
                collection.TryGetProperty("primaryThumbnail", out JsonElement primary))
            {
                image = primary;
                found = true;
            }
            else if (contentImage.TryGetProperty("thumbnailViewModel", out JsonElement thumb))
            {
                image = thumb;
                found = true;
            }
        }

        if (!found)
        {
            // list rows carry thumbnailViewModel directly
            image = node;
        }

        if (!image.TryGetProperty("image", out JsonElement imageNode))
            return null;

        return ExtractFromThumbnailSources(imageNode);
    }

    /// <summary>thumbnailViewModel.image.sources[] — takes the largest by declared width.</summary>
    private static string? ExtractFromThumbnailSources(JsonElement imageNode)
    {
        if (!imageNode.TryGetProperty("sources", out JsonElement sources) ||
            sources.ValueKind != JsonValueKind.Array ||
            sources.GetArrayLength() == 0)
            return null;

        JsonElement best = sources[0];
        int bestWidth = 0;
        foreach (JsonElement entry in sources.EnumerateArray())
        {
            int width = entry.TryGetProperty("width", out JsonElement w) && w.ValueKind == JsonValueKind.Number
                ? (int)w.GetDouble()
                : 0;
            if (width >= bestWidth)
            {
                bestWidth = width;
                best = entry;
            }
        }

        return best.TryGetProperty("url", out JsonElement url) &&
               url.ValueKind == JsonValueKind.String &&
               !url.GetString()
                   .StartsWith("//i.ytimg.com/manifest", StringComparison.Ordinal)
            ? url.GetString()
            : null;
    }

    /// <summary>lockupViewModel metadata text: "42 songs" / creator line (yt-dlp contentMetadataViewModel path).</summary>
    private static string? ExtractLockupMetadataText(JsonElement node)
    {
        // lockupViewModel.metadata.lockupMetadataViewModel.metadata.
        //   contentMetadataViewModel.metadataRows[].metadataParts[].text.content
        if (node.TryGetProperty("metadata", out JsonElement metadata) &&
            metadata.TryGetProperty("lockupMetadataViewModel", out JsonElement lmvm))
        {
            if (lmvm.TryGetProperty("metadata", out JsonElement lmvmMetadata) &&
                lmvmMetadata.TryGetProperty("contentMetadataViewModel", out JsonElement cmvm) &&
                ExtractMetadataRowsText(cmvm) is string viaCmvm)
                return viaCmvm;

            // Some shapes keep the rows directly on the metadata ViewModel
            return ExtractMetadataRowsText(lmvm);
        }

        return ExtractMetadataRowsText(node);
    }

    /// <summary>Walks metadataRows[].metadataParts[].text.content for readable text.</summary>
    private static string? ExtractMetadataRowsText(JsonElement node)
    {
        if (!node.TryGetProperty("metadataRows", out JsonElement rows) ||
            rows.ValueKind != JsonValueKind.Array)
            return null;

        List<string> parts = new();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("metadataParts", out JsonElement rowParts) ||
                rowParts.ValueKind != JsonValueKind.Array)
                continue;
            foreach (JsonElement part in rowParts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out JsonElement text) &&
                    text.TryGetProperty("content", out JsonElement content) &&
                    content.ValueKind == JsonValueKind.String)
                    parts.Add(content.GetString() ?? "");
            }
        }
        return parts.Count > 0 ? string.Join(" • ", parts.Where(p => p.Length > 0)) : null;
    }

    /// <summary>musicResponsiveListItemViewModel primaryText/secondaryText → text content.</summary>
    private static string? ExtractViewModelText(JsonElement node, string key)
    {
        if (!node.TryGetProperty(key, out JsonElement field))
            return null;

        if (field.TryGetProperty("content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.String)
            return content.GetString();

        // content runs: { content: "text", … }[] — join
        if (field.TryGetProperty("runs", out JsonElement runs) &&
            runs.ValueKind == JsonValueKind.Array)
        {
            StringBuilder sb = new();
            foreach (JsonElement run in runs.EnumerateArray())
            {
                if (run.TryGetProperty("content", out JsonElement rc) &&
                    rc.ValueKind == JsonValueKind.String)
                    sb.Append(rc.GetString());
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        return null;
    }

    /// <summary>Depth-bounded recursive walk for a playlist browseId under onTap/innertubeCommand routing.</summary>
    private static string? WalkForBrowseId(JsonElement node, int depth)
    {
        if (depth > 8 || node.ValueKind != JsonValueKind.Object)
            return null;

        if (node.TryGetProperty("browseEndpoint", out JsonElement browse) &&
            browse.TryGetProperty("browseId", out JsonElement id) &&
            id.ValueKind == JsonValueKind.String &&
            IsPlaylistBrowseId(id.GetString() ?? ""))
            return id.GetString();

        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                if (WalkForBrowseId(prop.Value, depth + 1) is string found)
                    return found;
            }
        }
        return null;
    }

    /// <summary>True when the node yields a videoId by any known route (used by the specials' emptiness check).</summary>
    private static bool HasVideoId(JsonElement node)
    {
        return ExtractVideoId(node) != null;
    }

    /// <summary>VL-prefixed browse ids (library grid) → bare playlist id (the app's PlaylistId format).</summary>
    private static string NormalizePlaylistId(string browseId) =>
        browseId.StartsWith("VL", StringComparison.Ordinal) ? browseId[2..] : browseId;

    /// <summary>
    /// One shelf row → video. Handles both row renderers YouTube Music uses on
    /// playlist pages: musicResponsiveListItemRenderer (playlist page default)
    /// and playlistPanelVideoRenderer (queue/panel shape). videoId can sit in
    /// playlistItemData, navigationEndpoint, flexColumns runs, or the play
    /// overlay (kaset "Playable rows" list).
    /// </summary>
    /// <summary>
    /// Collects playlist track rows from a browse (or continuation) response,
    /// walking the page STRUCTURALLY so both response eras work:
    ///   Renderer era — the classic page nests rows at
    ///     contents.twoColumnBrowseResultsRenderer.secondaryContents.
    ///     sectionListRenderer.contents[0].musicPlaylistShelfRenderer.contents
    ///     (a naive FindFirstArray(root,"contents") grabs the sectionList's
    ///     wrapper array — section objects, not rows — and finds nothing;
    ///     reproduced live). Continuation pages return the rows directly in
    ///     continuationContents.musicPlaylistShelfContinuation.contents or
    ///     appendContinuationItemsAction.continuationItems.
    ///   ViewModel era — rows sit in section/items arrays of
    ///     musicShelfViewModel / sectionListViewModel / gridViewModel.
    /// Returns the row ARRAY that was parsed (for continuation-token
    /// lookup), or default when none found.
    /// </summary>
    private static JsonElement CollectPlaylistShelfRows(JsonElement root, List<YouTubePlaylistVideo> videos)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return default;

        // Structural walk first: named shelf containers (classic page) or the
        // first array that yields rows (ViewModel page). Never let the
        // generic FindContinuationItems fallback grab some unrelated items
        // array (header buttons etc.) before the real shelf had its chance.
        JsonElement rows = FindRowArray(root, videos, depth: 0);
        if (rows.ValueKind == JsonValueKind.Array)
            return rows;

        // Continuation-response shapes (their rows are the array directly).
        JsonElement items = FindContinuationItems(root);
        if (items.ValueKind == JsonValueKind.Array)
        {
            CollectRowArray(items, videos);
            return items;
        }

        return default;
    }

    /// <summary>Feed one row-array through CollectShelfRow.</summary>
    private static void CollectRowArray(JsonElement items, List<YouTubePlaylistVideo> videos)
    {
        if (items.ValueKind != JsonValueKind.Array)
            return;
        foreach (JsonElement item in items.EnumerateArray())
            CollectShelfRow(item, videos);
    }

    /// <summary>
    /// Breadth-first-ish structural search for the track-row array: at each
    /// object node, test its array properties — the first array whose items
    /// yield videos (or a continuationItem row) is the shelf contents.
    /// Skips menu/overlay/metadata sub-objects so thumbnail overlays and
    /// menus can't shadow the real rows.
    /// </summary>
    private static JsonElement FindRowArray(JsonElement node, List<YouTubePlaylistVideo> videos, int depth)
    {
        if (depth > 12 || node.ValueKind != JsonValueKind.Object)
            return default;

        // Prefer named shelf containers (classic page: the shelf renderer's
        // own "contents" array; ViewModel page: shelf/section "items").
        string[] containerKeys = { "musicPlaylistShelfRenderer", "musicShelfRenderer", "playlistVideoListRenderer" };
        foreach (string key in containerKeys)
        {
            if (node.TryGetProperty(key, out JsonElement container) &&
                container.ValueKind == JsonValueKind.Object)
            {
                if (container.TryGetProperty("contents", out JsonElement contents) &&
                    contents.ValueKind == JsonValueKind.Array)
                {
                    int before = videos.Count;
                    CollectRowArray(contents, videos);
                    if (videos.Count > before)
                        return contents;
                }
            }
        }

        // Any object node's array property whose elements parse as rows.
        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array ||
                prop.Name is "menu" or "menus" or "overlays" or "badges" or "metadataRows" or "buttons")
                continue;

            // Reject arrays that are obviously not row collections.
            JsonElement arr = prop.Value;
            if (arr.GetArrayLength() == 0)
                continue;

            int before = videos.Count;
            CollectRowArray(arr, videos);
            if (videos.Count > before)
                return arr; // found the rows — stop searching this subtree
        }

        // Descend object properties (arrays descended via the loop above only
        // when they contain rows; otherwise recurse into each element).
        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                JsonElement found = FindRowArray(prop.Value, videos, depth + 1);
                if (found.ValueKind == JsonValueKind.Array)
                    return found;
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in prop.Value.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                        continue;
                    JsonElement found = FindRowArray(element, videos, depth + 1);
                    if (found.ValueKind == JsonValueKind.Array)
                        return found;
                }
            }
        }
        return default;
    }

    private static void CollectShelfRow(JsonElement item, List<YouTubePlaylistVideo> videos)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;

        if (item.TryGetProperty("continuationItemRenderer", out _))
            return; // the page's continuation token row — not a video

        if (item.TryGetProperty("musicResponsiveListItemRenderer", out JsonElement row))
        {
            if (ExtractVideo(row) is YouTubePlaylistVideo video)
                videos.Add(video);
            return;
        }

        if (item.TryGetProperty("playlistPanelVideoRenderer", out JsonElement panel))
        {
            if (ExtractVideo(panel) is YouTubePlaylistVideo video)
                videos.Add(video);
            return;
        }

        // musicTwoRowItemRenderer appears on some mixed shelves
        if (item.TryGetProperty("musicTwoRowItemRenderer", out JsonElement twoRow))
        {
            if (ExtractVideo(twoRow) is YouTubePlaylistVideo video)
                videos.Add(video);
            return;
        }

        // New-format rows: musicResponsiveListItemViewModel (LM/WL shelves on
        // accounts in the ViewModel rollout) and lockupViewModel video cards.
        if (item.TryGetProperty("musicResponsiveListItemViewModel", out JsonElement mrlvm))
        {
            if (ExtractVideo(mrlvm) is YouTubePlaylistVideo video)
                videos.Add(video);
            return;
        }

        if (item.TryGetProperty("lockupViewModel", out JsonElement lockup))
        {
            if (ExtractVideo(lockup) is YouTubePlaylistVideo video)
                videos.Add(video);
        }
    }

    /// <summary>Extracts one video row (videoId, title, duration, thumbnail) from a renderer element.</summary>
    private static YouTubePlaylistVideo? ExtractVideo(JsonElement renderer)
    {
        string? videoId = ExtractVideoId(renderer);
        if (string.IsNullOrEmpty(videoId))
            return null;

        // Classic playlist rows (musicResponsiveListItemRenderer) carry NO
        // "title" property — the title text lives in
        // flexColumns[0].musicResponsiveListItemFlexColumnRenderer.text.runs,
        // the artist in flexColumns[1], and the duration in
        // fixedColumns[0] ("3:55"). Without the flexColumns routes every
        // imported track arrived as "Untitled" (v1.5 regression — verified
        // against the real live page).
        string title = ExtractTitle(renderer)
                       ?? ExtractFlexColumnText(renderer, 0)
                       ?? ExtractLockupTitle(renderer)
                       ?? ExtractViewModelText(renderer, "primaryText")
                       ?? "Untitled";

        TimeSpan? duration = ExtractDuration(renderer)
                             ?? ExtractLockupDuration(renderer)
                             ?? ParseFixedColumnDuration(renderer);

        string? thumb = ExtractThumbnail(renderer)
                        ?? ExtractLockupThumbnail(renderer)
                        ?? ExtractViewModelText(renderer, "tertiaryText")
                        ?? $"https://img.youtube.com/vi/{videoId}/mqdefault.jpg";

        return new YouTubePlaylistVideo(
            title,
            $"https://www.youtube.com/watch?v={videoId}",
            thumb,
            duration);
    }

    /// <summary>ViewModel-era duration: thumbnail badges ("3:32") or overlay text.</summary>
    private static TimeSpan? ExtractLockupDuration(JsonElement renderer)
    {
        // contentImage.thumbnailViewModel.overlays[].thumbnailBottomOverlayViewModel.badges[]
        //   .thumbnailBadgeViewModel.text — the classic duration badge spot.
        if (renderer.TryGetProperty("contentImage", out JsonElement contentImage) &&
            contentImage.TryGetProperty("thumbnailViewModel", out JsonElement thumb))
        {
            if (thumb.TryGetProperty("overlays", out JsonElement overlays) &&
                overlays.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement overlay in overlays.EnumerateArray())
                {
                    if (!overlay.TryGetProperty("thumbnailBottomOverlayViewModel", out JsonElement bottom))
                        continue;
                    if (!bottom.TryGetProperty("badges", out JsonElement badges) ||
                        badges.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (JsonElement badge in badges.EnumerateArray())
                    {
                        if (badge.TryGetProperty("thumbnailBadgeViewModel", out JsonElement tbvm) &&
                            tbvm.TryGetProperty("text", out JsonElement text) &&
                            text.ValueKind == JsonValueKind.String &&
                            TryParseTimestamp(text.GetString(), out TimeSpan d))
                            return d;
                    }
                }
            }
        }

        return null;
    }

    // ── generic field walkers (JsonElement traversal, defensive at every step) ──

    private static string? ExtractVideoId(JsonElement renderer)
    {
        // playlistItemData.videoId
        if (renderer.TryGetProperty("playlistItemData", out JsonElement playlistItemData) &&
            playlistItemData.TryGetProperty("videoId", out JsonElement v1) &&
            v1.ValueKind == JsonValueKind.String)
            return v1.GetString();

        // navigationEndpoint.watchEndpoint.videoId
        if (renderer.TryGetProperty("navigationEndpoint", out JsonElement nav) &&
            nav.TryGetProperty("watchEndpoint", out JsonElement watch) &&
            watch.TryGetProperty("videoId", out JsonElement v2) &&
            v2.ValueKind == JsonValueKind.String)
            return v2.GetString();

        // title.runs[0].navigationEndpoint.watchEndpoint.videoId
        if (renderer.TryGetProperty("title", out JsonElement title) &&
            title.TryGetProperty("runs", out JsonElement runs) &&
            runs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement run in runs.EnumerateArray())
            {
                if (run.TryGetProperty("navigationEndpoint", out JsonElement nav2) &&
                    nav2.TryGetProperty("watchEndpoint", out JsonElement watch2) &&
                    watch2.TryGetProperty("videoId", out JsonElement v3) &&
                    v3.ValueKind == JsonValueKind.String)
                    return v3.GetString();
            }
        }

        // overlay → musicPlayButtonRenderer.playNavigationEndpoint.watchEndpoint.videoId
        if (renderer.TryGetProperty("overlay", out JsonElement overlay) &&
            WalkForVideoId(overlay) is string v4)
            return v4;

        // ViewModel-era rows: lockupViewModel.contentId with a VIDEO content
        // type; musicResponsiveListItemViewModel routes via onTap command runs.
        if (renderer.TryGetProperty("contentType", out JsonElement contentType) &&
            contentType.ValueKind == JsonValueKind.String &&
            contentType.GetString() == "LOCKUP_CONTENT_TYPE_VIDEO" &&
            renderer.TryGetProperty("contentId", out JsonElement contentId) &&
            contentId.ValueKind == JsonValueKind.String)
            return contentId.GetString();

        if (WalkForVideoId(renderer) is string v5)
            return v5;

        return null;
    }

    /// <summary>Depth-bounded recursive walk for a videoId under any play-button overlay.</summary>
    private static string? WalkForVideoId(JsonElement node, int depth = 0)
    {
        if (depth > 8 || node.ValueKind != JsonValueKind.Object)
            return null;

        if (node.TryGetProperty("watchEndpoint", out JsonElement watch) &&
            watch.TryGetProperty("videoId", out JsonElement v) &&
            v.ValueKind == JsonValueKind.String)
            return v.GetString();

        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                if (WalkForVideoId(prop.Value, depth + 1) is string found)
                    return found;
            }
        }
        return null;
    }

    private static string? ExtractTitle(JsonElement renderer)
    {
        // title.runs[].text (concatenated)
        if (renderer.TryGetProperty("title", out JsonElement title) &&
            title.ValueKind == JsonValueKind.Object)
            return ExtractTitleRuns(title);

        // playlistPanelVideoRenderer keeps plain text titles sometimes
        if (renderer.TryGetProperty("title", out JsonElement simple) &&
            simple.ValueKind == JsonValueKind.String)
            return simple.GetString();

        return null;
    }

    /// <summary>
    /// Playlist card → browse id: navigationEndpoint.browseEndpoint.browseId
    /// (library grid cards carry it already VL-prefixed) or, failing that,
    /// the title runs' own navigation. Null for non-playlist tiles (the
    /// "New playlist" tile has no browseId at all).
    /// </summary>
    private static string? ExtractBrowseId(JsonElement renderer)
    {
        // navigationEndpoint.browseEndpoint.browseId
        if (renderer.TryGetProperty("navigationEndpoint", out JsonElement nav) &&
            nav.TryGetProperty("browseEndpoint", out JsonElement browse) &&
            browse.TryGetProperty("browseId", out JsonElement id) &&
            id.ValueKind == JsonValueKind.String)
            return id.GetString();

        // title.runs[].navigationEndpoint.browseEndpoint.browseId
        if (renderer.TryGetProperty("title", out JsonElement title) &&
            title.TryGetProperty("runs", out JsonElement runs) &&
            runs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement run in runs.EnumerateArray())
            {
                if (run.TryGetProperty("navigationEndpoint", out JsonElement nav2) &&
                    nav2.TryGetProperty("browseEndpoint", out JsonElement browse2) &&
                    browse2.TryGetProperty("browseId", out JsonElement id2) &&
                    id2.ValueKind == JsonValueKind.String)
                    return id2.GetString();
            }
        }

        return null;
    }

    private static string ExtractTitleRuns(JsonElement titleElement)
    {
        if (!titleElement.TryGetProperty("runs", out JsonElement runs) ||
            runs.ValueKind != JsonValueKind.Array)
            return string.Empty;

        StringBuilder sb = new();
        foreach (JsonElement run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("text", out JsonElement text) &&
                text.ValueKind == JsonValueKind.String)
                sb.Append(text.GetString());
        }
        return sb.ToString();
    }

    private static string? ExtractSubtitle(JsonElement renderer)
    {
        // musicTwoRowItemRenderer.subtitle.runs[].text → "Playlist • 42 songs"
        if (renderer.TryGetProperty("subtitle", out JsonElement subtitle) &&
            subtitle.ValueKind == JsonValueKind.Object)
        {
            string text = ExtractTitleRuns(subtitle);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        return null;
    }

    /// <summary>musicTwoRowItemRenderer "Playlist • N songs" subtitle → owned flag (owned rows say so; saved ones mention the creator).</summary>
    private static bool ExtractOwnedFlag(JsonElement renderer)
    {
        if (renderer.TryGetProperty("menu", out JsonElement menu))
        {
            // The own-playlist card's menu carries a playlistEditorEndpoint entry
            // (ytmusicapi parse_playlist "owned" check).
            if (WalkForKey(menu, "playlistEditorEndpoint", 0))
                return true;
        }
        return false;
    }

    /// <summary>True when key exists anywhere under node (depth-bounded).</summary>
    private static bool WalkForKey(JsonElement node, string key, int depth)
    {
        if (depth > 8 || node.ValueKind != JsonValueKind.Object)
            return false;

        if (node.TryGetProperty(key, out _))
            return true;

        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array &&
                WalkForKey(prop.Value, key, depth + 1))
                return true;
        }
        return false;
    }

    private static string? ExtractThumbnail(JsonElement renderer)
    {
        // thumbnail.musicThumbnailRenderer.thumbnail.thumbnails[last]
        if (renderer.TryGetProperty("thumbnail", out JsonElement thumbnail))
        {
            string? viaMusic = thumbnail.TryGetProperty("musicThumbnailRenderer", out JsonElement music)
                ? ExtractFromThumbnailsArray(music)
                : null;
            if (viaMusic != null) return viaMusic;

            string? direct = ExtractFromThumbnailsArray(thumbnail);
            if (direct != null) return direct;
        }

        return null;
    }

    private static string? ExtractFromThumbnailsArray(JsonElement node)
    {
        // node.thumbnail.thumbnails — take the LARGEST entry (best quality).
        if (!node.TryGetProperty("thumbnail", out JsonElement thumbs) ||
            !thumbs.TryGetProperty("thumbnails", out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() == 0)
            return null;

        JsonElement best = array[0];
        int bestWidth = 0;
        foreach (JsonElement entry in array.EnumerateArray())
        {
            int width = entry.TryGetProperty("width", out JsonElement w) && w.ValueKind == JsonValueKind.Number
                ? (int)w.GetDouble()
                : 0;
            if (width >= bestWidth)
            {
                bestWidth = width;
                best = entry;
            }
        }

        return best.TryGetProperty("url", out JsonElement url) &&
               url.ValueKind == JsonValueKind.String &&
               url.GetString() is string u && !u.StartsWith("//i.ytimg.com/manifest")
            ? u
            : null;
    }

    /// <summary>
    /// Text of a classic playlist row's flex column (title in column 0,
    /// artist in column 1) — runs concatenated, plain content strings kept.
    /// Handles both musicResponsiveListItemFlexColumnRenderer and the older
    /// musicResponsiveColumnRenderer key.
    /// </summary>
    private static string? ExtractFlexColumnText(JsonElement renderer, int columnIndex)
    {
        if (!renderer.TryGetProperty("flexColumns", out JsonElement flexColumns) ||
            flexColumns.ValueKind != JsonValueKind.Array ||
            flexColumns.GetArrayLength() <= columnIndex)
            return null;

        JsonElement column = flexColumns[columnIndex];
        foreach (string key in new[] { "musicResponsiveListItemFlexColumnRenderer", "musicResponsiveColumnRenderer" })
        {
            if (!column.TryGetProperty(key, out JsonElement flex) ||
                !flex.TryGetProperty("text", out JsonElement text))
                continue;

            // Classic: text.runs[].text concatenated.
            string? viaRuns = text.ValueKind == JsonValueKind.Object ? ExtractTitleRuns(text) : null;
            if (!string.IsNullOrWhiteSpace(viaRuns))
                return viaRuns;

            // Some variants ship text.content / a plain string.
            if (text.TryGetProperty("content", out JsonElement content) &&
                content.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(content.GetString()))
                return content.GetString();
            if (text.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(text.GetString()))
                return text.GetString();
        }
        return null;
    }

    /// <summary>Duration from a classic row's fixedColumns[0] ("3:55").</summary>
    private static TimeSpan? ParseFixedColumnDuration(JsonElement renderer)
    {
        if (!renderer.TryGetProperty("fixedColumns", out JsonElement fixedColumns) ||
            fixedColumns.ValueKind != JsonValueKind.Array ||
            fixedColumns.GetArrayLength() == 0)
            return null;

        JsonElement column = fixedColumns[0];
        foreach (string key in new[] { "musicResponsiveListItemFixedColumnRenderer", "musicResponsiveColumnRenderer" })
        {
            if (!column.TryGetProperty(key, out JsonElement flex) ||
                !flex.TryGetProperty("text", out JsonElement text))
                continue;

            string? t = text.ValueKind == JsonValueKind.Object ? ExtractTitleRuns(text) : text.GetString();
            if (t != null && t.Length <= 8 && TryParseTimestamp(t, out TimeSpan d))
                return d;
        }
        return null;
    }

    private static TimeSpan? ExtractDuration(JsonElement renderer)
    {
        // playlistPanelVideoRenderer.lengthText → "3:32"
        if (renderer.TryGetProperty("lengthText", out JsonElement lengthText))
        {
            string? text = lengthText.ValueKind == JsonValueKind.String
                ? lengthText.GetString()
                : ExtractTitleRuns(lengthText);
            if (TryParseTimestamp(text, out TimeSpan duration))
                return duration;
        }

        // musicResponsiveListItemRenderer: duration lives in flexColumns after
        // the title (the column whose text parses as a timestamp).
        if (renderer.TryGetProperty("flexColumns", out JsonElement flexColumns) &&
            flexColumns.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement column in flexColumns.EnumerateArray())
            {
                if (column.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out JsonElement flex) &&
                    flex.TryGetProperty("text", out JsonElement text) &&
                    text.ValueKind == JsonValueKind.Object)
                {
                    string t = ExtractTitleRuns(text);
                    if (t.Length <= 8 && TryParseTimestamp(t, out TimeSpan d))
                        return d;
                }
            }
        }

        return null;
    }

    private static bool TryParseTimestamp(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // "3:32" / "1:03:32" / "45" (seconds) — tolerate surrounding text.
        string cleaned = text.Trim();
        string[] parts = cleaned.Split(':');
        if (parts.Length is < 1 or > 3)
            return false;

        int[] numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), out numbers[i]) || numbers[i] < 0)
                return false;
        }

        value = numbers.Length switch
        {
            1 => TimeSpan.FromSeconds(numbers[0]),
            2 => new TimeSpan(0, numbers[0], numbers[1]),
            _ => new TimeSpan(numbers[0], numbers[1], numbers[2])
        };
        return true;
    }

    // ── page-layout walkers ──

    /// <summary>Finds the first array-valued property with the given name, at any depth (≤12).</summary>
    private static JsonElement FindFirstArray(JsonElement node, string name, int depth = 0)
    {
        JsonElement found = default;
        if (node.ValueKind != JsonValueKind.Object || depth > 12)
            return found;

        if (node.TryGetProperty(name, out JsonElement direct) &&
            direct.ValueKind == JsonValueKind.Array)
            return direct;

        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                JsonElement inner = FindFirstArray(prop.Value, name, depth + 1);
                if (inner.ValueKind == JsonValueKind.Array)
                    return inner;
            }
        }
        return found;
    }

    /// <summary>Library-page continuation token (both eras): last row's continuation item, or a sibling continuationCommand.</summary>
    private static string? FindContinuationToken(JsonElement root)
    {
        JsonElement grid = FindFirstArray(root, "items");
        if (grid.ValueKind == JsonValueKind.Array)
        {
            string? token = FindContinuationTokenInShelf(grid);
            if (token != null)
                return token;
        }

        // ViewModel-era grids keep a "continuations" sibling array on the
        // section node (same place ytmusicapi's get_continuations looks for
        // the Renderer shape); walk for any continuationCommand token.
        return WalkForContinuationToken(root);
    }

    private static string? FindContinuationTokenInShelf(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            return null;

        // The classic last-row continuationItemRenderer
        foreach (JsonElement item in items.EnumerateArray().Reverse())
        {
            if (item.TryGetProperty("continuationItemRenderer", out JsonElement cont))
            {
                if (cont.TryGetProperty("continuationEndpoint", out JsonElement endpoint) &&
                    endpoint.TryGetProperty("continuationCommand", out JsonElement command) &&
                    command.TryGetProperty("token", out JsonElement token) &&
                    token.ValueKind == JsonValueKind.String)
                    return token.GetString();

                // commandExecutorCommand nesting (ytmusicapi COMMAND_EXECUTOR_COMMANDS)
                if (WalkForContinuationToken(cont) is string viaCommands)
                    return viaCommands;
            }
            break;
        }

        // ViewModel-era grids: the last row is a continuationItemViewModel.
        foreach (JsonElement item in items.EnumerateArray().Reverse())
        {
            if (item.TryGetProperty("continuationItemViewModel", out JsonElement vm))
            {
                if (WalkForContinuationToken(vm) is string viaVm)
                    return viaVm;
            }
            break;
        }

        return null;
    }

    private static string? WalkForContinuationToken(JsonElement node, int depth = 0)
    {
        if (depth > 8 || node.ValueKind != JsonValueKind.Object)
            return null;

        if (node.TryGetProperty("continuationCommand", out JsonElement command) &&
            command.TryGetProperty("token", out JsonElement token) &&
            token.ValueKind == JsonValueKind.String)
            return token.GetString();

        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                if (WalkForContinuationToken(prop.Value, depth + 1) is string found)
                    return found;
            }
        }
        return null;
    }

    /// <summary>Continuation response items: onResponseReceivedActions[0].appendContinuationItemsAction.continuationItems (ytmusicapi CONTINUATION_ITEMS).</summary>
    private static JsonElement FindContinuationItems(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return default;

        if (root.TryGetProperty("continuationContents", out JsonElement contents))
        {
            // continuationContents.gridContinuation.items or .musicPlaylistShelfContinuation.contents
            if (contents.TryGetProperty("musicPlaylistShelfContinuation", out JsonElement shelf))
                return shelf.TryGetProperty("contents", out JsonElement c) ? c : default;
            if (contents.TryGetProperty("gridContinuation", out JsonElement grid))
                return grid.TryGetProperty("items", out JsonElement i) ? i : default;
            // ViewModel-era continuations: gridViewModel.items sibling
            if (contents.TryGetProperty("gridViewModel", out JsonElement gridVm) &&
                gridVm.TryGetProperty("items", out JsonElement vmItems) &&
                vmItems.ValueKind == JsonValueKind.Array)
                return vmItems;
            return default;
        }

        if (root.TryGetProperty("onResponseReceivedActions", out JsonElement actions) &&
            actions.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement action in actions.EnumerateArray())
            {
                if (action.TryGetProperty("appendContinuationItemsAction", out JsonElement append) &&
                    append.TryGetProperty("continuationItems", out JsonElement items) &&
                    items.ValueKind == JsonValueKind.Array)
                    return items;
            }
        }

        // ViewModel-era continuation responses sometimes deliver the next
        // items directly under contents.sectionListViewModel/…/items — the
        // generic structural walk in ParsePlaylistGrid handles the cards
        // either way, so hand back the first items array found.
        return FindFirstArray(root, "items");
    }

    // ── playlist header (VL page metadata) ──

    private static string? ParsePlaylistHeaderTitle(JsonElement root)
    {
        // MusicDetailHeader / musicPlaylistShelf header shapes: header.title.runs
        JsonElement header = FindFirstObject(root, "musicDetailHeaderRenderer", 0);
        if (header.ValueKind == JsonValueKind.Object)
        {
            if (header.TryGetProperty("title", out JsonElement title) &&
                title.ValueKind == JsonValueKind.Object)
            {
                string t = ExtractTitleRuns(title);
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
        }

        // Some playlists use sectionListRenderer → header → musicResponsiveHeaderRenderer.title.runs
        JsonElement responsive = FindFirstObject(root, "musicResponsiveHeaderRenderer", 0);
        if (responsive.ValueKind == JsonValueKind.Object)
        {
            if (responsive.TryGetProperty("title", out JsonElement title2) &&
                title2.ValueKind == JsonValueKind.Object)
            {
                string t = ExtractTitleRuns(title2);
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
            // title as a plain string (some cohorts).
            if (responsive.TryGetProperty("title", out JsonElement titlePlain) &&
                titlePlain.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(titlePlain.GetString()))
                return titlePlain.GetString();
        }

        // ViewModel era: pageHeaderViewModel.title.legacyTitleViewModel.title.text
        // (or .runs). The header text itself is the playlist name.
        JsonElement pageHeader = FindFirstObject(root, "pageHeaderViewModel", 0);
        if (pageHeader.ValueKind == JsonValueKind.Object &&
            pageHeader.TryGetProperty("title", out JsonElement pvTitle))
        {
            if (pvTitle.TryGetProperty("legacyTitleViewModel", out JsonElement legacy) &&
                legacy.TryGetProperty("title", out JsonElement inner))
            {
                string? t = inner.ValueKind == JsonValueKind.Object
                    ? (ExtractTitleRuns(inner) is string r && !string.IsNullOrWhiteSpace(r) ? r : null)
                    : inner.GetString();
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
            // pageHeaderViewModel.title.dynamicTextViewModel.text
            if (pvTitle.TryGetProperty("dynamicTextViewModel", out JsonElement dyn) &&
                dyn.TryGetProperty("text", out JsonElement dynText) &&
                dynText.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(dynText.GetString()))
                return dynText.GetString();
            // plain string title
            if (pvTitle.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(pvTitle.GetString()))
                return pvTitle.GetString();
        }

        return null;
    }

    private static string? ParsePlaylistHeaderThumbnail(JsonElement root)
    {
        JsonElement header = FindFirstObject(root, "musicDetailHeaderRenderer", 0);
        if (header.ValueKind == JsonValueKind.Object &&
            header.TryGetProperty("thumbnail", out JsonElement thumbnail))
        {
            string? url = ExtractFromThumbnailsArray(thumbnail);
            if (url != null) return url;
        }

        // musicResponsiveHeaderRenderer (the shape the LIVE classic page
        // serves): thumbnail.musicThumbnailRenderer.thumbnail.thumbnails — the
        // thumbs array sits one level DEEPER than the old code looked, so the
        // playlist icon always came back empty (v1.5 regression).
        JsonElement responsive = FindFirstObject(root, "musicResponsiveHeaderRenderer", 0);
        if (responsive.ValueKind == JsonValueKind.Object)
        {
            string? viaMusic = ExtractThumbnail(responsive);
            if (viaMusic != null) return viaMusic;

            if (responsive.TryGetProperty("thumbnail", out JsonElement thumb2))
            {
                string? url = ExtractFromThumbnailsArray(thumb2);
                if (url != null) return url;
            }
        }

        // ViewModel era: pageHeaderViewModel.heroImage.sources[].url.
        JsonElement pageHeader = FindFirstObject(root, "pageHeaderViewModel", 0);
        if (pageHeader.ValueKind == JsonValueKind.Object &&
            pageHeader.TryGetProperty("heroImage", out JsonElement hero) &&
            hero.TryGetProperty("sources", out JsonElement sources) &&
            sources.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement source in sources.EnumerateArray())
            {
                if (source.TryGetProperty("url", out JsonElement url) &&
                    url.ValueKind == JsonValueKind.String)
                    return url.GetString();
            }
        }

        return null;
    }

    private static string? ParsePlaylistHeaderTrackCount(JsonElement root)
    {
        JsonElement header = FindFirstObject(root, "musicDetailHeaderRenderer", 0);
        if (header.ValueKind == JsonValueKind.Object)
        {
            if (header.TryGetProperty("subtitle", out JsonElement subtitle) &&
                subtitle.ValueKind == JsonValueKind.Object)
            {
                string text = ExtractTitleRuns(subtitle);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }

            // The live classic page carries "182 tracks" in secondSubtitle
            // (views • tracks • total duration), not subtitle.
            if (header.TryGetProperty("secondSubtitle", out JsonElement second) &&
                second.ValueKind == JsonValueKind.Object)
            {
                string text = ExtractTitleRuns(second);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }

        JsonElement responsive = FindFirstObject(root, "musicResponsiveHeaderRenderer", 0);
        if (responsive.ValueKind == JsonValueKind.Object &&
            responsive.TryGetProperty("secondSubtitle", out JsonElement second2) &&
            second2.ValueKind == JsonValueKind.Object)
        {
            string text = ExtractTitleRuns(second2);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        // ViewModel era: pageHeaderViewModel.metadata rows → find a row whose
        // text mentions "song"/"track"/"video" counts.
        JsonElement pageHeader = FindFirstObject(root, "pageHeaderViewModel", 0);
        if (pageHeader.ValueKind == JsonValueKind.Object &&
            pageHeader.TryGetProperty("metadata", out JsonElement metadata) &&
            metadata.TryGetProperty("content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement row in content.EnumerateArray())
            {
                if (!row.TryGetProperty("parts", out JsonElement parts) ||
                    parts.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("text", out JsonElement text) ||
                        text.ValueKind != JsonValueKind.Object)
                        continue;
                    string t = text.TryGetProperty("content", out JsonElement c) &&
                               c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
                    if (t.Contains("song", StringComparison.OrdinalIgnoreCase) ||
                        t.Contains("track", StringComparison.OrdinalIgnoreCase) ||
                        t.Contains("video", StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }
        }

        return null;
    }

    private static JsonElement FindFirstObject(JsonElement node, string name, int depth)
    {
        JsonElement found = default;
        if (node.ValueKind != JsonValueKind.Object || depth > 12)
            return found;

        if (node.TryGetProperty(name, out JsonElement direct) &&
            direct.ValueKind == JsonValueKind.Object)
            return direct;

        foreach (JsonProperty prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                JsonElement inner = FindFirstObject(prop.Value, name, depth + 1);
                if (inner.ValueKind == JsonValueKind.Object)
                    return inner;
            }
        }
        return found;
    }

    private static string? FirstVideoThumbnail(IReadOnlyList<YouTubePlaylistVideo> videos) =>
        videos.Count > 0 ? videos[0].ThumbnailUrl : null;

    private static void Trace(string message)
    {
        Debug.WriteLine(message);
#if ANDROID
        Android.Util.Log.Info("Musicthingy", message);
#endif
    }
}
