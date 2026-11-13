using CommunityToolkit.Maui.Behaviors;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Musicthingy.Services.Accounts;

namespace Musicthingy.Views;

// ────────────────────────────────────────────────────────────────────────────
// YouTubeAccountsPopup — the account menu as a REAL popup.
//
// v5 shipped the account menu as a ContextMenuSheet bottom sheet (the same
// one the track/playlist menus use) — but Services.SheetManager.Host only
// exists on the main page, so when the menu was opened from the Settings
// view the sheet host was null and NOTHING appeared ("a bottom sheet isn't
// visible in settings view"). This class uses the same MCT Popup base class
// as the app's own TextEntryDialog / EnumPickerDialog, with the same visual
// language (Border + CardColor + RoundRectangle 28, TitleColor/MutedColor
// text, IconTintColorBehavior-tinted lucide icons, pill primary button).
// MCT popups attach to the WINDOW, not to a specific page — they show from
// any page, Settings included.
//
// Result strings (set on the Result property when a row is tapped; the
// caller reads popup.Result after the page.ShowPopupAsync await completes):
//   account:<id> — the tapped account (switch to it, or remove it when the
//                  popup was opened with removeMode: true)
//   weblogin     — "Sign in with Google (in-app browser)"
//   devicecode   — "Sign in with Google (device code)"
//   cookies      — "Paste cookies / OpenTune block"
//   remove       — "Remove an account"
//   null         — dismissed (tap outside / Cancel)
// ────────────────────────────────────────────────────────────────────────────
public class YouTubeAccountsPopup : Popup
{
    public const string ResultWebLogin = "weblogin";
    public const string ResultDeviceCode = "devicecode";
    public const string ResultCookieImport = "cookies";
    public const string ResultRemove = "remove";
    public const string ResultAccountPrefix = "account:";

    /// <summary>
    /// What the popup "returned": a result string when a row was tapped,
    /// null on Cancel / tap-outside. Read by the caller after the
    /// page.ShowPopupAsync(popup) await completes. See CloseWithResult for
    /// why the result travels on the instance instead of through CloseAsync.
    /// </summary>
    public string? Result { get; private set; }

    public YouTubeAccountsPopup(AccountManager accounts, bool removeMode = false)
    {
        // Same popup setup as the app's own dialogs (TextEntryDialog).
        Padding = new Thickness(0);
        BackgroundColor = Colors.Transparent;
        CanBeDismissedByTappingOutsideOfPopup = true;

        var ytAccounts = accounts.GetAccounts(MusicPlatform.YouTube).ToList();
        var active = accounts.GetActive(MusicPlatform.YouTube);
        string? activeId = active?.Id;

        string title = removeMode ? "Remove Account" : "YouTube Accounts";
        string subtitle = removeMode
            ? "Tap the account you want to remove."
            : active == null
                ? "No account linked — pick a sign-in method below."
                : ytAccounts.Count > 1
                    ? $"Active: {active.DisplayName} · {ytAccounts.Count} accounts linked."
                    : $"Active: {active.DisplayName}";

        // ── Header ────────────────────────────────────────────────────────
        var titleLabel = new Label { Text = title, FontSize = 24 };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TitleColor");

        var subtitleLabel = new Label
        {
            Text = subtitle,
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

        if (ytAccounts.Count > 0 && !removeMode)
            rows.Add(SectionLabel("ACCOUNTS"));

        foreach (var account in ytAccounts)
        {
            var target = account;
            bool isActive = account.Id == activeId;

            string icon = removeMode ? "trash_2_icon.png"
                         : isActive ? "check_icon.png"
                         : "user_icon.png";
            string title2 = isActive ? $"{target.DisplayName} (active)" : target.DisplayName;
            string kind = target.YouTubeKind == YouTubeAuthKind.OAuth ? "Google sign-in" : "cookies";

            rows.Add(Row(icon, title2, kind, danger: removeMode,
                YouTubeAccountsPopup.ResultAccountPrefix + target.Id));
        }

        if (!removeMode)
        {
            if (ytAccounts.Count > 0)
                rows.Add(Separator());

            rows.Add(SectionLabel("ADD ACCOUNT"));
            rows.Add(Row("log_in_icon.png", "Sign in with Google (in-app browser)",
                "Opens the Google sign-in page; cookies are captured automatically.",
                danger: false, ResultWebLogin));
            rows.Add(Row("key_icon.png", "Sign in with Google (device code)",
                "Enter a code at google.com/activate on any device.",
                danger: false, ResultDeviceCode));
            rows.Add(Row("clipboard_icon.png", "Paste cookies / OpenTune block",
                "View, copy or replace the current cookies.",
                danger: false, ResultCookieImport));

            if (ytAccounts.Count > 0)
            {
                rows.Add(Separator());
                rows.Add(Row("trash_2_icon.png", "Remove an account",
                    "Unlink an account from the app.",
                    danger: true, ResultRemove));
            }
        }

        var scroller = new ScrollView
        {
            Content = rows,
            MaximumHeightRequest = 340
        };

        // ── Buttons (same style as EnumPickerDialog) ──────────────────────
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
    // Row factory — same geometry as the settings rows (icon column + text
    // column) that fixed the "icon centered over the text" bug in v5.
    // ─────────────────────────────────────────────────────────────────────

    private Grid Row(string icon, string title, string? subtitle, bool danger, string result)
    {
        var row = new Grid { Padding = new Thickness(0, 12), ColumnSpacing = 16 };
        row.AddColumnDefinition(new ColumnDefinition(GridLength.Auto));
        row.AddColumnDefinition(new ColumnDefinition(GridLength.Star));

        // Danger rows (the remove flow) are red. The app's own theme XAML has
        // no "Danger" resource, so GetThemeColor falls through to a fixed red.
        Color dangerColor = GetThemeColor("Danger", Color.FromArgb("#E5484D"));

        var image = new Image
        {
            Source = icon,
            WidthRequest = 24,
            HeightRequest = 24,
            VerticalOptions = LayoutOptions.Center
        };
        // IconTintColorBehavior is a Behavior — it inherits BindableObject
        // directly (skipping Element/VisualElement), and the SetDynamicResource
        // that the XAML rows use through {DynamicResource} markup
        // (ActionTemplateView.xaml: TintColor="{DynamicResource TitleColor}")
        // is NOT callable on it from C# (CS1061). Resolve the tint color by
        // hand with the same GetThemeColor pattern LyricsView.xaml.cs uses.
        var tint = new IconTintColorBehavior
        {
            TintColor = danger
                ? dangerColor
                : GetThemeColor("TitleColor", Color.FromArgb("#F8F9FA"))
        };
        image.Behaviors.Add(tint);
        row.Add(image);

        var text = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Spacing = 2 };
        var titleLabel = new Label { FontSize = 16, Text = title };
        if (danger)
            titleLabel.TextColor = dangerColor;
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
        // Grid.Add(view, column, row): icon column 0, text column 1 — the
        // same Auto,* geometry as the settings rows.
        row.Add(text, 1, 0);

        row.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => CloseWithResult(result))
        });

        return row;
    }

    private static Label SectionLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(0, 10, 0, 2),
            CharacterSpacing = 1
        };
        label.SetDynamicResource(Label.TextColorProperty, "MutedColor");
        return label;
    }

    private static BoxView Separator()
    {
        var separator = new BoxView { HeightRequest = 1, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.6 };
        separator.SetDynamicResource(BoxView.ColorProperty, "BorderColor");
        return separator;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Closing with a result. The toolkit's NON-generic Popup base class only
    // offers CloseAsync(CancellationToken) — the value-carrying overload
    // CloseAsync(T result, CancellationToken) belongs to the generic Popup<T>
    // subclass, which this popup does not use (so CloseAsync(result) cannot
    // compile on Popup). The result therefore travels on the popup instance:
    // set Result, then close with the plain CloseAsync(). The caller awaits
    // page.ShowPopupAsync(popup) — that task completes when the popup closes
    // (row tap / Cancel / tap-outside) — and then reads popup.Result.
    // Tap-outside dismisses without running any code here, so Result stays
    // null in that path, which is exactly the "dismissed" contract.
    // ─────────────────────────────────────────────────────────────────────────────
    private void CloseWithResult(string? result)
    {
        Result = result;
        _ = CloseAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Theme colors in C# — the same lookup pattern as the app's own
    // LyricsView.xaml.cs GetThemeColor(key, fallback) helper: resources are
    // resolved by hand with TryGetValue (application dictionaries first, then
    // any merged theme dictionaries). Values stored as AppThemeBinding (the
    // {AppThemeBinding Light=…, Dark=…} XAML form) are unwrapped for the
    // current theme. The popup is short-lived, so a one-shot lookup at
    // construction time is enough — no live theme switching needed.
    // ─────────────────────────────────────────────────────────────────────────────
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
            Color? fromMerged = FindColor(merged, key, depth + 1);
            if (fromMerged is not null)
                return fromMerged;
        }

        return null;
    }
}
