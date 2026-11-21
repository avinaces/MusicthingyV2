using CommunityToolkit.Mvvm.ComponentModel;
using Musicthingy.Services;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Musicthingy.Models;

/// <summary>
/// Round-7/8 addition: a SEPARATE high-resolution artwork source for the full
/// player's big cover, split out of PlaylistItem so the file stays a drop-in
/// (PlaylistItem.cs is upstream's; this partial carries ONLY the new members).
///
/// Why separate: ItemIconSource is the 44dp row/list thumbnail pipeline
/// (decoded small, scaled to 44*density, cached at that size). The full
/// player's ArtworkCard is ~360dp - reusing the same source is the blur the
/// user reported. FullPlayerArtworkSource instead:
///   1. binds immediately to the SAVED icon (mqdefault.jpg via ItemIconSource)
///      so the pane always has something to show, then
///   2. upgrades in the background via MediaResolutionService (maxresdefault
///      1280x720, hq720, sddefault chain) when the track has YouTube
///      identity - round 8: the id is now extracted from the SAVED
///      thumbnail URL too (round 7 never matched img.youtube.com URLs, so
///      the upgrade silently never ran), and generated pixel covers are
///      re-rendered at the big-card pixel size, and
///   3. falls back permanently to the saved icon when nothing better exists.
///
/// CACHE SEAM: the in-memory _sharedHiResArtworkCache below is the future
/// disk-cache seam (keys are video ids / stable per-icon keys; values are
/// decoded ImageSources). Swapping its backing store for
/// MediaResolutionService's cache touches nothing else.
///
/// Round-8 CS0757 note: upstream PlaylistItem.cs ALREADY declares AND
/// implements `partial void OnIconUrlChanged(string value)` - a second
/// implementing declaration in this file is a compile error (CS0757: "a
/// partial method may not have multiple implementing declarations"). This
/// file therefore NEVER declares partial method implementations; the
/// invalidation hook below rides on this instance's OWN PropertyChanged
/// stream instead (ObservableObject raises it for every [ObservableProperty]).
/// </summary>
public partial class PlaylistItem
{
    // Shared across all items: one hi-res cover per cache key, process-wide.
    private static readonly ConcurrentDictionary<string, ImageSource> _sharedHiResArtworkCache = new();

    private ImageSource? _fullPlayerArtworkSource;
    private bool _isUpgradingArtwork;
    private bool _iconChangeHooked;

    [JsonIgnore]
    public ImageSource? FullPlayerArtworkSource
    {
        get
        {
            HookIconChange();

            if (_fullPlayerArtworkSource != null)
                return _fullPlayerArtworkSource;

            // Fast path: the upgraded cover was resolved before.
            string? cacheKey = GetHiResCacheKey();
            if (cacheKey != null && _sharedHiResArtworkCache.TryGetValue(cacheKey, out var cached))
            {
                _fullPlayerArtworkSource = cached;
                return cached;
            }

            // Show the SAVED icon immediately (never a blank cover) while the
            // upgrade runs in the background.
            _fullPlayerArtworkSource = ItemIconSource;

            if (!_isUpgradingArtwork)
            {
                _isUpgradingArtwork = true;
                _ = UpgradeArtworkAsync(cacheKey);
            }

            return _fullPlayerArtworkSource;
        }
    }

    /// <summary>
    /// Invalidation without partial methods (the CS0757 fix): ObservableObject
    /// raises PropertyChanged for IconUrl on every setter run, including the
    /// upstream OnIconUrlChanged path - we simply listen for it once per
    /// instance. An edited icon (data-URI swap, file pick) resets the hi-res
    /// source so the NEXT get rebinds to the new saved icon and re-runs the
    /// upgrade.
    /// </summary>
    private void HookIconChange()
    {
        if (_iconChangeHooked)
            return;
        _iconChangeHooked = true;
        PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(IconUrl))
            {
                _fullPlayerArtworkSource = null;
                _isUpgradingArtwork = false;
                OnPropertyChanged(nameof(FullPlayerArtworkSource));
            }
        };
    }

    /// <summary>
    /// The cache/upgrade key. The video id when the track (or its saved icon
    /// URL) carries YouTube identity; for generated covers the stable TITLE_
    /// key (the same seed IconGenerator uses, so a regenerated hi-res cover
    /// matches across items); ICON_&lt;url&gt; for real image icons.
    /// ROUND 9: probes Url AND IconUrl for the id (round 8 probed only one of
    /// them, so a YouTube track whose icon was saved as a data-URI / local
    /// file never matched and the upgrade silently never ran - the user's
    /// "low res icons still show up").
    /// </summary>
    private string? GetHiResCacheKey()
    {
        string? videoId = ExtractVideoId(Url) ?? ExtractVideoId(IconUrl);
        if (videoId != null)
            return videoId;

        if (string.IsNullOrEmpty(IconUrl))
            return $"TITLE_{Title}"; // generated cover: seed == title

        return $"ICON_{IconUrl}";
    }

    /// <summary>
    /// Round 8: video-id extraction that actually works for the SAVED icon
    /// URLs in the wild. IconUrl for YouTube imports is the mqdefault
    /// THUMBNAIL URL (https://img.youtube.com/vi/&lt;id&gt;/mqdefault.jpg), and
    /// YoutubeExplode's VideoId.TryParse rejects thumbnail URLs in that
    /// shape - round 7 never matched, so the upgrade silently never ran
    /// (the user's "still low res" report). Delegates to
    /// MediaResolutionService.ExtractVideoId (the single implementation).
    /// </summary>
    private static string? ExtractVideoId(string? url)
        => MediaResolutionService.ExtractVideoId(url);

    private async Task UpgradeArtworkAsync(string? cacheKey)
    {
        try
        {
            if (cacheKey == null)
                return;

            if (_sharedHiResArtworkCache.TryGetValue(cacheKey, out var cached))
            {
                await SetArtworkAsync(cached);
                return;
            }

            // ~360dp cover at 3x density ~= 1080px - ask for at least 600 so
            // even a 640-wide sddefault qualifies, while the 120x90 gray
            // placeholder img.youtube.com serves for missing sizes does not.
            // ROUND 9: probe Url AND IconUrl (the id lives in the track Url
            // when the icon was saved as a data-URI / local file).
            string? videoId = ExtractVideoId(Url) ?? ExtractVideoId(IconUrl);
            byte[]? bytes = videoId == null
                ? null
                : await MediaResolutionService.TryGetArtworkByIdAsync(videoId, minPixelWidth: 600);

            // ROUND 9: no network art (or no id) - judge the SAVED icon by its
            // actual pixel size, which is what the user asked for: "it should
            // check if the icon is low res, and if it is, then fetch a higher
            // res version or something". Decision table:
            //   - generated pixel cover (IconGenerator output: an 8-logical
            //     BMP at 44dp = 44*density px, or no IconUrl at all):
            //     re-render at the big-card size - the generator's whole
            //     design is a nearest-neighbor logical grid at EXACT physical
            //     size, so upscaling its small raster is the reported blur.
            //   - real image below the big-card width: fetchable when a video
            //     id exists (handled above); with NO id there is nothing
            //     better to fetch - the saved icon stays (fallback rule).
            //   - real image already >= big-card pixels: keep it (perfect fit).
            if (bytes == null && IsGeneratedCover(cacheKey))
            {
                // uiSize is in DP and IconGenerator multiplies by the screen
                // density internally (actualSize = uiSize * density) - passing
                // pre-multiplied pixels double-scaled and overflowed the
                // raster (round-8 bug: GenerateIcon(Title, 8, physical)).
                ImageSource generated = IconGenerator.GenerateIcon(Title, 8, 360);
                _sharedHiResArtworkCache[cacheKey] = generated;
                await SetArtworkAsync(generated);
                return;
            }

            if (bytes == null)
            {
                // Measured saved icon: only leave the low-res icon in place
                // when it is genuinely the best available art (no id to fetch
                // by). A real image that already measures big enough was
                // never "low res" - nothing to do here either way.
                return; // keep the saved icon - it is the fallback by design
            }

            ImageSource source = ImageSource.FromStream(() => new MemoryStream(bytes));
            _sharedHiResArtworkCache[cacheKey] = source;
            await SetArtworkAsync(source);
        }
        catch
        {
            // Network/decode failure: the saved icon stays.
        }
        finally
        {
            _isUpgradingArtwork = false;
        }
    }

    /// <summary>
    /// ROUND 9: is this item's saved art a GENERATED pixel cover rather than
    /// a real image? Three signatures, in cheapness order:
    ///   1. no IconUrl at all - ItemIconSource's generator branch (round-8
    ///      path, cache key TITLE_),
    ///   2. a data:image BMP whose bytes carry IconGenerator's exact BMP
    ///      signature (headers "BM", 54-byte pixel offset, 32bpp, square,
    ///      side exactly 44*density or 110/48 - the uiSizes the generator is
    ///      called with),
    ///   3. a TITLE_ cache key (the no-IconUrl case already decided upstream).
    /// Measuring the bytes matters because "the artwork was saved as an icon"
    /// is exactly a small data-URI the user picked - and its pixel size is
    /// the ground truth for "low res".
    /// </summary>
    private bool IsGeneratedCover(string? cacheKey)
    {
        if (cacheKey != null && cacheKey.StartsWith("TITLE_", StringComparison.Ordinal))
            return true; // no IconUrl: the generator branch by definition

        if (string.IsNullOrEmpty(IconUrl) || !IconUrl.StartsWith("data:image", StringComparison.Ordinal))
            return false;

        try
        {
            string b64 = IconUrl[(IconUrl.IndexOf(',') + 1)..];
            byte[] data = Convert.FromBase64String(b64);

            // IconGenerator emits a raw 54-header BMP: "BM", DIB size 40,
            // pixel offset 54, 1 plane, 32bpp, square side.
            if (data.Length <= 54 || data[0] != (byte)'B' || data[1] != (byte)'M')
                return false;
            int dibSize = BitConverter.ToInt32(data, 14);
            int pixelOffset = BitConverter.ToInt32(data, 10);
            int planes = BitConverter.ToInt16(data, 26);
            int bpp = BitConverter.ToInt16(data, 28);
            int width = BitConverter.ToInt32(data, 18);
            int height = Math.Abs(BitConverter.ToInt32(data, 22));
            if (dibSize != 40 || pixelOffset != 54 || planes != 1 || bpp != 32 ||
                width != height || width < 8)
                return false;

            // Square 32bpp BMP with the exact generator uiSize pixels (the
            // app renders icons at 44dp; density multiplies to 44/88/132...).
            return width % 44 == 0 || width % 48 == 0 || width % 110 == 0;
        }
        catch
        {
            return false; // not a valid data-URI payload: treat as a real image
        }
    }

    private async Task SetArtworkAsync(ImageSource source)
    {
        // OnPropertyChanged must land on the UI thread; the upgrade runs on
        // a background continuation.
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _fullPlayerArtworkSource = source;
            OnPropertyChanged(nameof(FullPlayerArtworkSource));
        });
    }
}
