using Microsoft.Maui.Controls;

namespace Musicthingy.Services;

public static class SheetManager
{
    public static Views.SlideUpSheetHost? Host { get; set; }

    private static Func<object?, Task>? _currentCloseAction;

    public static async Task<T?> ShowAsync<T>(ContentView sheetContent)
    {
        if (Host == null) return default;

        var tcs = new TaskCompletionSource<T?>();

        if (sheetContent is Views.IBottomSheet sheet)
        {
            sheet.CloseAction = async (result) =>
            {
                await Host.HideAsync(sheetContent);
                _currentCloseAction = null;

                if (result is T typedResult) tcs.TrySetResult(typedResult);
                else tcs.TrySetResult(default);
            };

            // Fix: Wrap the void Action in a block that returns a Task
            _currentCloseAction = (res) =>
            {
                sheet.CloseAction(res);
                return Task.CompletedTask;
            };
        }

        await Host.ShowAsync(sheetContent);
        return await tcs.Task;
    }

    public static async Task CloseCurrentAsync(object? result = null)
    {
        if (_currentCloseAction != null)
        {
            await _currentCloseAction(result);
        }
    }
}