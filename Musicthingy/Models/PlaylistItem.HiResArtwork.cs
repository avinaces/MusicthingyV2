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
    /// </summary>
    private string? GetHiResCacheKey()
    {
        string probeUrl = string.IsNullOrEmpty(IconUrl) ? Url : IconUrl;
        string? videoId = ExtractVideoId(probeUrl);
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
            byte[]? bytes = await MediaResolutionService.TryGetArtworkAsync(
                string.IsNullOrEmpty(IconUrl) ? Url : IconUrl, minPixelWidth: 600);

            // Round 8: no YouTube identity (or the fetch failed). The
            // generated pixel covers are the remaining blur: an 8x8 logical
            // grid stretched to ~360dp looks smeared, so re-render at the
            // big-card's physical pixel size (IconGenerator's whole design -
            // nearest-neighbor logical-grid raster at exact physical size).
            // Real saved images that are merely low-quality have nothing
            // better to fetch (no id) - the saved icon stays, per the
            // user's fallback rule.
            if (bytes == null && string.IsNullOrEmpty(IconUrl) &&
                cacheKey.StartsWith("TITLE_", StringComparison.Ordinal))
            {
                double density = 1.0;
                try
                {
                    density = DeviceDisplay.Current.MainDisplayInfo.Density;
                    if (density <= 0) density = 1.0;
                }
                catch { }

                int physical = (int)Math.Ceiling(360 * density);
                ImageSource generated = IconGenerator.GenerateIcon(Title, 8, physical);
                _sharedHiResArtworkCache[cacheKey] = generated;
                await SetArtworkAsync(generated);
                return;
            }

            if (bytes == null)
                return; // keep the saved icon - it is the fallback by design

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
