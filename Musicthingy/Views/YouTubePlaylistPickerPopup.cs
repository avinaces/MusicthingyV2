using CommunityToolkit.Maui.Behaviors;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Musicthingy.Services.YouTube;

namespace Musicthingy.Views;

// ────────────────────────────────────────────────────────────────────────────
// YouTubePlaylistPickerPopup — the "My YouTube playlists" browser.
//
// Lists the signed-in account's playlists (YouTubeAccountPlaylistsService →
// InnerTube browse FEmusic_liked_playlists) as thumbnail rows. Tapping a row
// resolves to that playlist's id (the heavy contents fetch happens later in
// the import pipeline, on a worker thread — this popup only picks).
//
// Visual language matches YouTubeAccountsPopup / ImportPlaylistMenuPopup.
// The list loads asynchronously: the popup shows "Loading your playlists…"
// first and swaps in the rows (or the failure / empty message) when the
// browse response lands.
// ────────────────────────────────────────────────────────────────────────────
public class YouTubePlaylistPickerPopup : Popup
{
    /// <summary>
    /// The tapped playlist's id (bare, no VL prefix); null when dismissed /
    /// nothing selected. Read by the caller after ShowPopupAsync completes.
    /// </summary>
    public string? ResultPlaylistId { get; private set; }

    /// <summary>
    /// The tapped playlist as listed (title / thumbnail / count text). The
    /// listing page already carried this metadata, so the import can use it
    /// to pre-fill the name+icon sheet even when the playlist PAGE header
    /// parses to nothing.
    /// </summary>
    public YouTubeAccountPlaylist? ResultPlaylist { get; private set; }

    private readonly VerticalStackLayout _rows;
    private readonly ActivityIndicator _spinner;
    private readonly Label _statusLabel;

    private List<YouTubeAccountPlaylist> _playlists = new();

    public YouTubePlaylistPickerPopup(
        YouTubeAccountPlaylistsService playlistsService,
        CancellationToken ct = default)
    {
        Padding = new Thickness(0);
        BackgroundColor = Colors.Transparent;
        CanBeDismissedByTappingOutsideOfPopup = true;

        var titleLabel = new Label { Text = "My YouTube Playlists", FontSize = 24 };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TitleColor");

        var subtitleLabel = new Label
        {
            Text = "Tap a playlist to import it.",
            FontSize = 14,
            LineBreakMode = LineBreakMode.WordWrap
        };
        subtitleLabel.SetDynamicResource(Label.TextColorProperty, "MutedColor");

        var header = new VerticalStackLayout
        {
            Spacing = 4,
            Children = { titleLabel, subtitleLabel }
        };

        _spinner = new ActivityIndicator
        {
            IsRunning = true,
            WidthRequest = 28,
            HeightRequest = 28,
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 24)
        };
        _spinner.SetDynamicResource(ActivityIndicator.ColorProperty, "PrimaryButtonColor");

        _statusLabel = new Label
        {
            Text = "Loading your playlists…",
            FontSize = 14,
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 8),
            LineBreakMode = LineBreakMode.WordWrap
        };
        _statusLabel.SetDynamicResource(Label.TextColorProperty, "MutedColor");

        _rows = new VerticalStackLayout { Spacing = 2 };

        var scroller = new ScrollView
        {
            Content = new VerticalStackLayout { Children = { _spinner, _statusLabel, _rows } },
            MaximumHeightRequest = 420
        };

        // ── Buttons ──────────────────────────────────────────────────────
        var cancelButton = new Button
        {
            Text = "Cancel",
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(16, 0),
            HorizontalOptions = LayoutOptions.End
        };
        cancelButton.SetDynamicResource(Button.TextColorProperty, "PrimaryButtonColor");
        cancelButton.Clicked += (_, _) => CloseWithResult(null);

        var buttons = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        buttons.AddColumnDefinition(new ColumnDefinition(GridLength.Star));
        buttons.Add(cancelButton);

        // ── Card ──────────────────────────────────────────────────────────
        var card = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(28) },
            WidthRequest = DeviceInfo.Current.Idiom == DeviceIdiom.Desktop ? 440 : 360,
            MaximumHeightRequest = 600,
            Padding = new Thickness(24),
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Children = { header, scroller, buttons }
            }
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "CardColor");

        Content = card;

        // Load off the UI thread; the popup itself renders immediately.
        _ = LoadPlaylistsAsync(playlistsService, ct);
    }

    private async Task LoadPlaylistsAsync(YouTubeAccountPlaylistsService service, CancellationToken ct)
    {
        IReadOnlyList<YouTubeAccountPlaylist>? playlists = null;
        try
        {
            playlists = await Task.Run(() => service.GetAccountPlaylistsAsync(ct), ct);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImportYouTube] playlist listing failed: {ex.Message}");
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _spinner.IsRunning = false;
            _spinner.IsVisible = false;

            if (playlists == null)
            {
                _statusLabel.Text =
                    "Could not load your playlists.\nCheck your YouTube sign-in in Settings and try again.\n(See [YTPL] lines in the debug log for why.)";
                return;
            }

            if (playlists.Count == 0)
            {
                // Not "no playlists" glibly — explain what was checked. The
                // [YTPL] log lines carry the server's actual answer.
                _statusLabel.Text =
                    "No playlists found on this account.\nThis covers your playlists, saved playlists,\nLiked Songs and Watch Later.\n(See [YTPL] lines in the debug log.)";
                return;
            }

            _statusLabel.IsVisible = false;
            _playlists = playlists.ToList();

            foreach (YouTubeAccountPlaylist playlist in playlists)
                _rows.Add(PlaylistRow(playlist));
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // Row: thumbnail | title + track count. Playlist thumbnails come from
    // YouTube's CDN (https://i.ytimg.com/... or /generated_... playlists use
    // the default) — Image caches by URI natively, no extra work needed.
    // ─────────────────────────────────────────────────────────────────────
    private Grid PlaylistRow(YouTubeAccountPlaylist playlist)
    {
        var row = new Grid { Padding = new Thickness(0, 8), ColumnSpacing = 14 };
        row.AddColumnDefinition(new ColumnDefinition(new GridLength(64)));
        row.AddColumnDefinition(new ColumnDefinition(GridLength.Star));

        // Thumbnails come from YouTube's CDN; playlists without artwork — and
        // the special lists — fall back to the app's own lucide icons:
        // Liked Songs → thumbs-up, Watch Later → clock, everything else →
        // the youtube glyph (lucide purged brand icons upstream, so the repo
        // carries its own youtube_icon.svg authored in lucide's exact format).
        string fallbackIcon = playlist.PlaylistId switch
        {
            "LM" => "thumbs_up_icon.png",
            "WL" => "clock_icon.png",
            _ => "youtube_icon.png"
        };
        var thumb = new Image
        {
            Source = playlist.ThumbnailUrl ?? fallbackIcon,
            WidthRequest = 64,
            HeightRequest = 40,
            Aspect = Aspect.AspectFill,
            VerticalOptions = LayoutOptions.Center
        };
        if (string.IsNullOrEmpty(playlist.ThumbnailUrl))
        {
            // Lucide icons are baked white (csproj TintColor) and re-tinted
            // per-theme here — the same pairing SettingsPage.xaml uses.
            thumb.WidthRequest = 28;
            thumb.HeightRequest = 28;
            thumb.HorizontalOptions = LayoutOptions.Center;
            var tint = new IconTintColorBehavior
            {
                TintColor = GetThemeColor("TitleColor", Color.FromArgb("#F8F9FA"))
            };
            thumb.Behaviors.Add(tint);
        }
        row.Add(thumb);

        var text = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Spacing = 2 };
        var titleLabel = new Label
        {
            FontSize = 15,
            Text = playlist.Title,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TitleColor");
        text.Add(titleLabel);

        string count = playlist.TrackCountText ?? playlist.PlaylistId switch
        {
            "LM" => "Every track you've liked on YouTube",
            "WL" => "Your YouTube Watch Later queue",
            _ => playlist.PlaylistId
        };
        var countLabel = new Label
        {
            FontSize = 13,
            Text = count,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        };
        countLabel.SetDynamicResource(Label.TextColorProperty, "MutedColor");
        text.Add(countLabel);

        row.Add(text, 1, 0);

        string playlistId = playlist.PlaylistId;
        row.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => CloseWithResult(playlistId, playlist))
        });

        return row;
    }

    private void CloseWithResult(string? playlistId, YouTubeAccountPlaylist? playlist = null)
    {
        ResultPlaylistId = playlistId;
        ResultPlaylist = playlist;
        _ = CloseAsync();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Icons: every image in this popup is a plain string resource name from
    // the app's lucide set (Resources/Images/lucide/*.svg → "name_icon.png"
    // after the resizetizer runs). Nothing is drawn in C#.
    // ─────────────────────────────────────────────────────────────────────

    // ── theme color lookup (same as the other popups) ──
    private static Color GetThemeColor(string key, Color fallback)
    {
        Color? found = FindColor(Application.Current?.Resources, key, depth: 0);
        return found ?? fallback;
    }

    private static Color? FindColor(ResourceDictionary? dictionary, string key, int depth)
    {
        if (dictionary is null || depth > 4)
            return null;

        if (dictionary.TryGetValue(key, out object? value))
        {
            if (value is Color solid)
                return solid;

            // AppThemeBinding is internal in .NET MAUI, so inspect Dark/Light/Default via reflection
            if (value is not null)
            {
                Type type = value.GetType();
                var darkProp = type.GetProperty("Dark");
                var lightProp = type.GetProperty("Light");
                var defaultProp = type.GetProperty("Default");

                if (darkProp != null || lightProp != null)
                {
                    object? dark = darkProp?.GetValue(value);
                    object? light = lightProp?.GetValue(value);
                    object? def = defaultProp?.GetValue(value);

                    bool isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
                    object? chosen = isDark
                        ? dark ?? light ?? def
                        : light ?? dark ?? def;

                    if (chosen is Color themedColor)
                        return themedColor;

                    if (chosen is string colorStr && Color.TryParse(colorStr, out Color parsedColor))
                        return parsedColor;
                }
            }
        }

        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
        {
            Color? found = FindColor(merged, key, depth + 1);
            if (found != null)
                return found;
        }

        return null;
    }
}
