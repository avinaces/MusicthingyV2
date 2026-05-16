using Microsoft.Maui.Controls;

namespace Musicthingy.Services;

public static class SheetManager
{
    // We bind this in MobileMainPage
    public static Views.SlideUpSheetHost? Host { get; set; }

    public static async Task<T?> ShowAsync<T>(ContentView sheetContent)
    {
        if (Host == null) return default;

        var tcs = new TaskCompletionSource<T?>();

        if (sheetContent is Views.IBottomSheet sheet)
        {
            sheet.CloseAction = async (result) =>
            {
                await Host.HideAsync(sheetContent);

                if (result is T typedResult) tcs.TrySetResult(typedResult);
                else tcs.TrySetResult(default);
            };
        }

        await Host.ShowAsync(sheetContent);
        return await tcs.Task;
    }
}