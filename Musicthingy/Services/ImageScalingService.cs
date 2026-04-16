using System;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;

namespace Musicthingy.Services;

public static partial class ImageScalingService
{
    public static async Task<ImageSource?> ScaleIfSmallAsync(byte[] imageBytes, double uiSize)
    {
        double density = 1.0;
        try
        {
            // Thread-safe density check
            if (MainThread.IsMainThread)
                density = DeviceDisplay.Current.MainDisplayInfo.Density;
            else
                await MainThread.InvokeOnMainThreadAsync(() => density = DeviceDisplay.Current.MainDisplayInfo.Density);

            if (density <= 0) density = 1.0;
        }
        catch { }

        int actualSize = (int)Math.Ceiling(uiSize * density);

        // Call the platform-specific partial method
        return await ScalePlatformAsync(imageBytes, actualSize);
    }

    // The platform folders will provide the implementation for this:
    private static partial Task<ImageSource?> ScalePlatformAsync(byte[] imageBytes, int actualSize);
}