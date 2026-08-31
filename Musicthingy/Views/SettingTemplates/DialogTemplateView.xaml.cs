using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Musicthingy.Settings;
using Musicthingy.Views.Dialogs;

namespace Musicthingy.Views.Templates;

public partial class DialogTemplateView : ContentView
{
    public DialogTemplateView() { InitializeComponent(); }

    private async void OnTapped(object sender, TappedEventArgs e)
    {
        if (BindingContext is ISetting setting)
        {
            Popup dialog = setting.DialogType switch
            {
                "ColorPicker" => new ColorPickerDialog(setting),
                "Picker" => new EnumPickerDialog(setting),
                _ => new TextEntryDialog(setting)
            };

            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page != null)
            {
                // Use ShowPopupAsync so the popup's layout pass completes before
                // user interaction begins. This fixes the issue where the color
                // wheel doesn't respond to touch and entries don't show typing.
                await page.ShowPopupAsync(dialog);
            }
        }
    }
}