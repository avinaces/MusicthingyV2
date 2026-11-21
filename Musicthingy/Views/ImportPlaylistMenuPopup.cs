using CommunityToolkit.Maui.Behaviors;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Musicthingy.Services.Accounts;

namespace Musicthingy.Views;

// ────────────────────────────────────────────────────────────────────────────
// ImportPlaylistMenuPopup — the "Import Playlist" button's chooser.
//
// Replaces the old direct-to-file-picker flow: tapping Import now offers
//   • Import from file        — the previous JSON file picker (unchanged)
//   • Paste a YouTube link    — a YouTube playlist URL import
//   • My YouTube playlists    — pick one of the signed-in account's own
//                                playlists (requires linked YouTube account)
//
// Same MCT Popup pattern as YouTubeAccountsPopup (Border + CardColor +
// RoundRectangle 28, TitleColor/MutedColor text, IconTintColorBehavior-tinted
// icons, results travel on the popup instance because the non-generic Popup
// cannot carry a value through CloseAsync).
// ────────────────────────────────────────────────────────────────────────────
public class ImportPlaylistMenuPopup : Popup
{
    public const string ResultFromFile = "file";
    public const string ResultFromLink = "link";
    public const string ResultFromAccount = "account";

    /// <summary>
    /// What the popup "returned": "file", "link" or "account" when a row was
    /// tapped; null when dismissed (Cancel / tap outside). Read by the caller
    /// after the page.ShowPopupAsync(popup) await completes.
    /// </summary>
    public string? Result { get; private set; }

    private readonly AccountManager? _accounts;

    public ImportPlaylistMenuPopup(AccountManager? accounts = null)
    {
        _accounts = accounts;

        Padding = new Thickness(0);
        BackgroundColor = Colors.Transparent;
        CanBeDismissedByTappingOutsideOfPopup = true;

        bool hasYouTubeAccount = accounts?.GetAccounts(MusicPlatform.YouTube).Any() == true;

        var titleLabel = new Label { Text = "Import Playlist", FontSize = 24 };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TitleColor");

        var subtitleLabel = new Label
        {
            Text = "Choose where to import from.",
            FontSize = 14,
            LineBreakMode = LineBreakMode.WordWrap
        };
        subtitleLabel.SetDynamicResource(Label.TextColorProperty, "MutedColor");

        var header = new VerticalStackLayout
        {
            Spacing = 4,
            Children = { titleLabel, subtitleLabel }
        };

        // ── Rows ──────────────────────────────────────────────────────────
        var rows = new VerticalStackLayout { Spacing = 2 };

        rows.Add(Row("folder_icon.png", "Import from file",
            "Pick one or more Musicthingy playlist JSON files.",
            ResultFromFile));

        rows.Add(Row("link_icon.png", "Paste a YouTube link",
            "Import any public YouTube playlist by its URL.",
            ResultFromLink));

        // The account row is only offered when an account is actually linked;
        // no sign-in teaser here — the app's account menu (Settings) owns that.
        // All three icons are lucide set members: folder_icon, link_icon and
        // youtube_icon (lucide purged brand icons upstream, so the repo carries
        // its own youtube_icon.svg authored in lucide's exact format — stroke
        // geometry, 24×24 viewBox, same header attributes).
        if (hasYouTubeAccount)
        {
            rows.Add(Row("youtube_icon.png", "My YouTube playlists",
                "Pick one of your account's playlists to import.",
                ResultFromAccount));
        }
        else
        {
            rows.Add(Row("youtube_icon.png", "My YouTube playlists",
                "Sign in with your YouTube account in Settings to enable this.",
                null, enabled: false));
        }

        var scroller = new ScrollView
        {
            Content = rows,
            MaximumHeightRequest = 400
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
            WidthRequest = DeviceInfo.Current.Idiom == DeviceIdiom.Desktop ? 400 : 340,
            MaximumHeightRequest = 560,
            Padding = new Thickness(24),
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Children = { header, scroller, buttons }
            }
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "CardColor");

        Content = card;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Row factory — identical geometry to YouTubeAccountsPopup.Row (icon
    // column + text column), with a disabled variant for the locked
    // account row (muted text, no tap).
    // ─────────────────────────────────────────────────────────────────────
    private Grid Row(string icon, string title, string? subtitle, string? result, bool enabled = true)
    {
        var row = new Grid { Padding = new Thickness(0, 12), ColumnSpacing = 16 };
        row.AddColumnDefinition(new ColumnDefinition(GridLength.Auto));
        row.AddColumnDefinition(new ColumnDefinition(GridLength.Star));

        Color disabledColor = GetThemeColor("MutedColor", Color.FromArgb("#9A9AA6"));

        var image = new Image
        {
            Source = icon,
            WidthRequest = 24,
            HeightRequest = 24,
            VerticalOptions = LayoutOptions.Center,
            Opacity = enabled ? 1.0 : 0.45
        };
        var tint = new IconTintColorBehavior
        {
            TintColor = GetThemeColor("TitleColor", Color.FromArgb("#F8F9FA"))
        };
        image.Behaviors.Add(tint);
        if (!enabled)
            tint.TintColor = disabledColor;
        row.Add(image);

        var text = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Spacing = 2 };
        var titleLabel = new Label { FontSize = 16, Text = title };
        if (!enabled)
            titleLabel.TextColor = disabledColor;
        else
            titleLabel.SetDynamicResource(Label.TextColorProperty, "TitleColor");
        text.Add(titleLabel);

        if (!string.IsNullOrEmpty(subtitle))
        {
            var subtitleLabel = new Label
            {
                FontSize = 13,
                Text = subtitle,
                LineBreakMode = LineBreakMode.WordWrap
            };
            subtitleLabel.SetDynamicResource(Label.TextColorProperty, "MutedColor");
            text.Add(subtitleLabel);
        }
        row.Add(text, 1, 0);

        if (enabled && result != null)
        {
            row.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => CloseWithResult(result))
            });
        }

        return row;
    }

    private void CloseWithResult(string? result)
    {
        Result = result;
        _ = CloseAsync();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Theme color lookup — same pattern as YouTubeAccountsPopup (resources
    // resolved by hand with TryGetValue, AppThemeBinding unwrapped).
    // ─────────────────────────────────────────────────────────────────────
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
