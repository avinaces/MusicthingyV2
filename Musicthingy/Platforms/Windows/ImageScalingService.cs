using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Musicthingy.Services;

public static partial class ImageScalingService
{
    private static partial async Task<ImageSource?> ScalePlatformAsync(byte[] imageBytes, int actualSize)
    {
        return await Task.Run(async () =>
        {
            using var ras = new InMemoryRandomAccessStream();
            using (var dw = new DataWriter(ras))
            {
                dw.WriteBytes(imageBytes);
                await dw.StoreAsync();

                // Tell the DataWriter to release the stream so it doesn't dispose it!
                dw.DetachStream();
            }

            ras.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(ras);

            if (decoder.PixelWidth <= 32 && decoder.PixelHeight <= 32)
            {
                // Added "using" here to prevent memory leaks
                using var outMs = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateForTranscodingAsync(outMs, decoder);

                encoder.BitmapTransform.ScaledWidth = (uint)actualSize;
                encoder.BitmapTransform.ScaledHeight = (uint)actualSize;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.NearestNeighbor;

                await encoder.FlushAsync();

                var size = outMs.Size;
                using var reader = new DataReader(outMs.GetInputStreamAt(0));

                await reader.LoadAsync((uint)size);
                var outBytes = new byte[size];
                reader.ReadBytes(outBytes);

                // Safe practice: Detach the reader too
                reader.DetachStream();

                return ImageSource.FromStream(() => new MemoryStream(outBytes));
            }
            return null;
        });
    }
}