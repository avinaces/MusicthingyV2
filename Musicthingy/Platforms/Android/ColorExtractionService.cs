using Android.Graphics;
using Musicthingy.Models;
using System.IO;

namespace Musicthingy.Services;

public partial class ColorExtractionService
{
    public partial async Task<Microsoft.Maui.Graphics.Color?> ExtractDominantColorAsync(Playlist playlist)
    {
        if (playlist == null || string.IsNullOrEmpty(playlist.IconUrl))
            return null;

        try
        {
            // WallpaperColors was introduced in Android 8.1 (API 27)
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.OMr1)
            {
                Bitmap? bitmap = null;

                // Load the image safely
                if (playlist.IconUrl.StartsWith("data:image"))
                {
                    var base64Data = playlist.IconUrl.Substring(playlist.IconUrl.IndexOf(',') + 1);
                    var bytes = Convert.FromBase64String(base64Data);
                    bitmap = await BitmapFactory.DecodeByteArrayAsync(bytes, 0, bytes.Length);
                }
                else if (File.Exists(playlist.IconUrl))
                {
                    bitmap = await BitmapFactory.DecodeFileAsync(playlist.IconUrl);
                }

                if (bitmap != null)
                {
                    // Step 1: Scale down dramatically for blazing fast performance
                    var scaledBitmap = Bitmap.CreateScaledBitmap(bitmap, 112, 112, false);

                    // Step 2: Extract colors natively
                    var wallpaperColors = Android.App.WallpaperColors.FromBitmap(scaledBitmap);
                    var primary = wallpaperColors?.PrimaryColor;

                    // Step 3: Cleanup unmanaged memory immediately
                    bitmap.Recycle();
                    scaledBitmap?.Recycle();

                    if (primary != null)
                    {
                        // Convert Android ColorObject to an integer
                        int argb = primary.ToArgb();

                        // Extract RGBA channels
                        byte a = (byte)((argb >> 24) & 0xFF);
                        byte r = (byte)((argb >> 16) & 0xFF);
                        byte g = (byte)((argb >> 8) & 0xFF);
                        byte b = (byte)(argb & 0xFF);

                        // Return the MAUI Color
                        return Microsoft.Maui.Graphics.Color.FromRgba(r, g, b, a);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Color Extraction Failed: {ex.Message}");
        }

        return null;
    }
}