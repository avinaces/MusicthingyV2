using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Android.Graphics;

namespace Musicthingy.Services;

public static partial class ImageScalingService
{
    private static partial async Task<ImageSource?> ScalePlatformAsync(byte[] imageBytes, int actualSize)
    {
        return await Task.Run(() =>
        {
            // Decode the original tiny image
            var bitmap = BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length);
            if (bitmap == null) return null;

            // Only perform the nearest-neighbor trick if the source is 32x32 or smaller
            if (bitmap.Width <= 32 && bitmap.Height <= 32)
            {
                // Create the scaled version. 
                // The 'false' parameter is the "filter" flag: false = Nearest Neighbor (Sharp)
                var scaledBitmap = Bitmap.CreateScaledBitmap(bitmap, actualSize, actualSize, false);

                byte[] scaledBytes;
                using (var outStream = new MemoryStream())
                {
                    scaledBitmap.Compress(Bitmap.CompressFormat.Png, 100, outStream);
                    // Convert to array so the lambda below can create a fresh stream on every reload
                    scaledBytes = outStream.ToArray();
                }

                // Native memory cleanup to prevent Java OutOfMemory errors during scrolling
                bitmap.Recycle();
                bitmap.Dispose();
                scaledBitmap.Recycle();
                scaledBitmap.Dispose();

                // Return a lambda that creates a new stream every time the UI needs to re-render
                return ImageSource.FromStream(() => new MemoryStream(scaledBytes));
            }

            // If not small enough to scale, clean up and return null 
            // so the shared code can use the original source
            bitmap.Recycle();
            bitmap.Dispose();
            return null;
        });
    }
}