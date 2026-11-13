using CommunityToolkit.Maui.Views;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls.Shapes;

namespace Musicthingy.Views;

// ────────────────────────────────────────────────────────────────────────────
// CookieImportPopup — "Paste Cookies" as a real popup, with the CURRENT
// cookies visible in a copyable format.
//
// The v5 flow used Page.DisplayPromptAsync — a single-line native input
// dialog that (a) showed nothing about the cookies already stored and
// (b) made multi-hundred-character cookie strings practically unusable.
// This popup pre-fills a multi-line Editor with the app's current YouTube
// auth in OpenTune's copyable ***FIELD*** = value block format:
//
//   ***INNERTUBE COOKIE*** = SID=...; __Secure-3PSID=...; ...
//   ***VISITOR DATA*** = ...
//   ***DATA SYNC ID*** = ...
//   ***PO TOKEN*** = ...
//
// The Editor is selectable text: long-press → Select All → Copy works
// natively, and the Copy button copies the whole box. The block can be
// pasted straight into OpenTune/InnerTune-style apps (same format), and
// anything pasted back here parses with the same OpenTuneAuthParser.
//
// Result: the (possibly edited) text on Import, null on Cancel / tap-outside.
// The result travels on the popup instance itself — see the comment above
// CloseWithResult for why (the toolkit's non-generic Popup cannot carry a
// value through CloseAsync).
// ────────────────────────────────────────────────────────────────────────────
public class CookieImportPopup : Popup
{
    private readonly Editor _editor;
    private readonly Button _copyButton;

    /// <summary>
    /// What the popup "returned": the (possibly edited) editor text on
    /// Import, null on Cancel / tap-outside. Read by the caller after the
    /// page.ShowPopupAsync(popup) await completes.
    /// </summary>
    public string? Result { get; private set; }

    public CookieImportPopup(string currentAuthBlock)
    {
        // Same popup setup as the app's own dialogs (TextEntryDialog).
        Padding = new Thickness(0);
        BackgroundColor = Colors.Transparent;
        CanBeDismissedByTappingOutsideOfPopup = true;

        var titleLabel = new Label { Text = "Paste Cookies", FontSize = 24 };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TitleColor");

        var infoLabel = new Label
        {
            Text = "The current cookies are shown below and can be copied. " +
                   "To import new ones, paste the full OpenTune block " +
                   "(all ***...*** lines) or a raw cookie string, then press Import.",
            FontSize = 14,
            LineBreakMode = LineBreakMode.WordWrap
        };
        infoLabel.SetDynamicResource(Label.TextColorProperty, "MutedColor");

        _editor = new Editor
        {
            Text = currentAuthBlock ?? "",
            HeightRequest = 200,
            Keyboard = Keyboard.Default,
            IsTextPredictionEnabled = false,
            IsSpellCheckEnabled = false,
            AutoSize = EditorAutoSizeOption.Disabled
        };
        _editor.SetDynamicResource(Editor.TextColorProperty, "TextColor");
        _editor.SetDynamicResource(Editor.BackgroundColorProperty, "SurfaceInput");

        // Hint shown when there are no cookies to pre-fill (Editor has no
        // Placeholder API — Entry does).
        var emptyHint = new Label
        {
            Text = "No cookies stored yet — paste them below.",
            FontSize = 13,
            IsVisible = string.IsNullOrEmpty(currentAuthBlock)
        };
        emptyHint.SetDynamicResource(Label.TextColorProperty, "MutedColor");

        // ── Buttons ───────────────────────────────────────────────────────
        _copyButton = new Button
        {
            Text = "Copy",
            CornerRadius = 8,
            HeightRequest = 44,
            Padding = new Thickness(16, 0)
        };
        _copyButton.SetDynamicResource(Button.BackgroundColorProperty, "CardColor");
        _copyButton.SetDynamicResource(Button.TextColorProperty, "TextColor");
        _copyButton.SetDynamicResource(Button.BorderColorProperty, "BorderColor");
        _copyButton.Clicked += async (_, _) => await CopyToClipboardAsync();

        var cancelButton = new Button
        {
            Text = "Cancel",
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(12, 0)
        };
        cancelButton.SetDynamicResource(Button.TextColorProperty, "PrimaryButtonColor");
        cancelButton.Clicked += (_, _) => CloseWithResult(null);

        var importButton = new Button
        {
            Text = "Import",
            CornerRadius = 100,
            Padding = new Thickness(16, 0)
        };
        importButton.SetDynamicResource(Button.BackgroundColorProperty, "PrimaryButtonColor");
        importButton.SetDynamicResource(Button.TextColorProperty, "PrimaryButtonText");
        importButton.Clicked += (_, _) => CloseWithResult(_editor.Text);

        var buttons = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnSpacing = 12 };
        buttons.AddColumnDefinition(new ColumnDefinition(GridLength.Star));
        buttons.AddColumnDefinition(new ColumnDefinition(GridLength.Auto));
        buttons.AddColumnDefinition(new ColumnDefinition(GridLength.Auto));
        buttons.Add(_copyButton, 0, 0);
        buttons.Add(cancelButton, 1, 0);
        buttons.Add(importButton, 2, 0);

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
                Children = { titleLabel, infoLabel, emptyHint, _editor, buttons }
            }
        };
        card.SetDynamicResource(Border.BackgroundColorProperty, "CardColor");

        Content = card;
    }

    // ────────────────────────────────────────────────────────────────────────────────
    // Closing with a result. The toolkit's NON-generic Popup base class only
    // offers CloseAsync(CancellationToken) — the value-carrying overload
    // CloseAsync(T result, CancellationToken) belongs to the generic Popup<T>
    // subclass, which this popup does not use (so CloseAsync(_editor.Text)
    // cannot compile on Popup). The result therefore travels on the popup
    // instance: set Result, then close with the plain CloseAsync(). The caller
    // awaits page.ShowPopupAsync(popup) — that task completes when the popup
    // closes (Import / Cancel / tap-outside) — and then reads popup.Result.
    // Tap-outside dismisses without running any code here, so Result stays
    // null in that path, which is exactly the "cancelled" contract.
    // ────────────────────────────────────────────────────────────────────────────────
    private void CloseWithResult(string? result)
    {
        Result = result;
        _ = CloseAsync();
    }

    private async Task CopyToClipboardAsync()
    {
        try
        {
            string text = _editor.Text;
            if (!string.IsNullOrEmpty(text))
                await Clipboard.Default.SetTextAsync(text);

            _copyButton.Text = "Copied";
            await Task.Delay(1200);
            _copyButton.Text = "Copy";
        }
        catch
        {
            // Clipboard can be unavailable (rare platform states); the user
            // can still select + copy manually in the Editor.
            _copyButton.Text = "Copy";
        }
    }
}
