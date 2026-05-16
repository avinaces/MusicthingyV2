namespace Musicthingy.Views;

public partial class SlideUpSheetHost : ContentView
{
    public SlideUpSheetHost() { InitializeComponent(); }

    public async Task ShowAsync(View content)
    {
        IsVisible = true;
        content.TranslationY = 2000; // Start safely off-screen
        SheetContainer.Children.Add(content);

        var tasks = new List<Task> { content.TranslateToAsync(0, 0, 300, Easing.CubicOut) };

        if (SheetContainer.Children.Count == 1) // Only fade backdrop for the first sheet
            tasks.Add(Backdrop.FadeToAsync(1, 250, Easing.CubicOut));

        await Task.WhenAll(tasks);
    }

    public async Task HideAsync(View content)
    {
        var tasks = new List<Task> { content.TranslateToAsync(0, 2000, 250, Easing.CubicIn) };

        if (SheetContainer.Children.Count == 1) // Fade out backdrop if it's the last sheet closing
            tasks.Add(Backdrop.FadeToAsync(0, 250, Easing.CubicIn));

        await Task.WhenAll(tasks);
        SheetContainer.Children.Remove(content);

        if (SheetContainer.Children.Count == 0) IsVisible = false;
    }

    private void OnBackdropTapped(object sender, EventArgs e)
    {
        // Tapping the background cancels the topmost open sheet
        if (SheetContainer.Children.LastOrDefault() is IBottomSheet activeSheet)
            activeSheet.CloseAction?.Invoke(null);
    }
}