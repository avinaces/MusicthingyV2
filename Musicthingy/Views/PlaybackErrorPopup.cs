using CommunityToolkit.Maui.Views;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls.Shapes;

namespace Musicthingy.Views;

// ────────────────────────────────────────────────────────────────────────────
// PlaybackErrorPopup — the playback-failure dialog showing the RAW resolve
// log (what every InnerTube client actually answered, the YoutubeExplode
// error and the [SABR] session lines), nothing interpreted — plus a Copy
// button for the whole log.
//
// Built on the toolkit's non-generic Popup like the app's other C# dialogs
// (see CookieImportPopup / TextEntryDialog). No result to carry: the popup
// only informs; OK / tap-outside close it.
// ────────────────────────────────────────────────────────────────────────────
public class PlaybackErrorPopup : Popup
{
    private const int MaxLogHeight = 420;

    private readonly string _log;
    private readonly Button _copyButton;

    public PlaybackErrorPopup(string log)
    {
        _log = log;

        // Same popup setup as the app's other dialogs.
        Padding = new Thickness(0);
        BackgroundColor = Colors.Transparent;
        CanBeDismissedByTappingOutsideOfPopup = true;

        var titleLabel = new Label { Text = "Playback Error", FontSize = 24 };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TitleColor");

        // The log itself — raw lines, monospaced feel via a smaller size and
        // no re-wrapping interpretation. Height follows the line count.
        int lineCount = log.Length == 0 ? 1 : log.Split('\n').Length;
        var logLabel = new Label
        {
            Text = log,
            FontSize = 13,
            LineBreakMode = LineBreakMode.CharacterWrap
        };
        logLabel.SetDynamicResource(Label.TextColorProperty, "TextColor");

        var scrollView = new ScrollView
        {
            Content = logLabel,
            HeightRequest = Math.Min(MaxLogHeight, Math.Max(96, lineCount * 19 + 16))
        };

        // ── Buttons ───────────────────────────────────────────────────────
        _copyButton = new Button
        {
            Text = "Copy Log",
            CornerRadius = 8,
            HeightRequest = 44,
            Padding = new Thickness(16, 0)
        };
        _copyButton.SetDynamicResource(Button.BackgroundColorProperty, "CardColor");
        _copyButton.SetDynamicResource(Button.TextColorProperty, "TextColor");
        _copyButton.SetDynamicResource(Button.BorderColorProperty, "BorderColor");
        _copyButton.Clicked += async (_, _) => await CopyLogAsync();

        var okButton = new Button
        {
            Text = "OK",
            CornerRadius = 100,
            Padding = new Thickness(16, 0)
        };
        okButton.SetDynamicResource(Button.BackgroundColorProperty, "PrimaryButtonColor");
        okButton.SetDynamicResource(Button.TextColorProperty, "PrimaryButtonText");
        okButton.Clicked += (_, _) => _ = CloseAsync();

        var buttons = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnSpacing = 12 };
        buttons.AddColumnDefinition(new ColumnDefinition(GridLength.Star));
        buttons.AddColumnDefinition(new ColumnDefinition(GridLength.Auto));
        buttons.Add(_copyButton, 0, 0);
        buttons.Add(okButton, 1, 0);

        // ── Card (same style as the app's dialogs) ────────────────────────
        var card = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(28) },
            WidthRequest = DeviceInfo.Current.Idiom == DeviceIdiom.Desktop ? 460 : 360,
            Padding = new Thickness(24),
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Children = { titleLabel, scrollView, buttons }
            }
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "CardColor");

        Content = card;
    }

    private async Task CopyLogAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(_log))
                await Clipboard.Default.SetTextAsync(_log);

            _copyButton.Text = "Copied";
            await Task.Delay(1200);
            _copyButton.Text = "Copy Log";
        }
        catch
        {
            // Clipboard can be unavailable (rare platform states) — reset the
            // label and let the user read the log in the popup.
            _copyButton.Text = "Copy Log";
        }
    }
}
