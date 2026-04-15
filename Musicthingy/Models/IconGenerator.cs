using Microsoft.Maui.Devices;

namespace Musicthingy.Models;

public static class IconGenerator
{
    // Replaced 'scale' with 'uiSize'
    public static ImageSource GenerateIcon(string seed, int logicalSize, double uiSize)
    {
        if (string.IsNullOrEmpty(seed)) seed = "default";

        uint hash = HashSeed(seed);
        int hue = (int)(hash % 360);
        int sat = 50 + (int)(hash % 40);
        int bgLight = 10 + (int)(hash % 10);
        int brightLight = 68 + (int)(hash % 18);
        int darkLight = 34 + (int)(hash % 16);

        Color bright = Color.FromHsla(((hue + 180) % 360) / 360.0, sat / 100.0, brightLight / 100.0);
        Color dark = Color.FromHsla(((hue + 180) % 360) / 360.0, sat / 100.0, darkLight / 100.0);

        // Get the actual screen density (e.g., Retina displays are 2x or 3x)
        double density = 1.0;
        try
        {
            density = DeviceDisplay.Current.MainDisplayInfo.Density;
            if (density <= 0) density = 1.0;
        }
        catch { }

        // Calculate the exact physical pixels needed so the OS doesn't scale/blur it
        int actualSize = (int)Math.Ceiling(uiSize * density);
        if (actualSize < logicalSize) actualSize = logicalSize;

        int byteCount = actualSize * actualSize * 4;
        byte[] bmpBytes = new byte[54 + byteCount];

        // BMP Header
        bmpBytes[0] = 66; // B
        bmpBytes[1] = 77; // M
        BitConverter.GetBytes(bmpBytes.Length).CopyTo(bmpBytes, 2);
        bmpBytes[10] = 54; // Pixel offset
        bmpBytes[14] = 40; // DIB Header size
        BitConverter.GetBytes(actualSize).CopyTo(bmpBytes, 18); // Width
        BitConverter.GetBytes(-actualSize).CopyTo(bmpBytes, 22); // Height (Negative = top-down)
        bmpBytes[26] = 1;  // Color Planes
        bmpBytes[28] = 32; // Bits per pixel

        int offset = 54;
        int half = logicalSize / 2;

        // Draw physical pixels using nearest-neighbor calculation
        for (uint y = 0; y < actualSize; y++)
        {
            // Map the physical Y pixel back to the logical grid
            uint logicalY = (uint)(y * logicalSize / actualSize);
            for (uint x = 0; x < actualSize; x++)
            {
                // Map the physical X pixel back to the logical grid
                uint logicalX = (uint)(x * logicalSize / actualSize);

                uint mirrorX = logicalX < half ? logicalX : (uint)(logicalSize - 1 - logicalX);
                uint noise = HashNoise(hash, mirrorX, logicalY);
                Color pixelColor = (noise % 100) < 50 ? bright : dark;

                bmpBytes[offset++] = (byte)(pixelColor.Blue * 255);
                bmpBytes[offset++] = (byte)(pixelColor.Green * 255);
                bmpBytes[offset++] = (byte)(pixelColor.Red * 255);
                bmpBytes[offset++] = 255; // Alpha
            }
        }

        return ImageSource.FromStream(() => new MemoryStream(bmpBytes));
    }

    private static uint HashSeed(string seed)
    {
        uint hash = 0;
        foreach (char c in seed) hash = c + ((hash << 5) - hash);
        return hash;
    }

    private static uint HashNoise(uint seed, uint x, uint y)
    {
        uint value = seed ^ ((x + 1) * 0x1f123bb5) ^ ((y + 1) * 0x9e3779b1);
        value ^= value >> 15;
        value *= 0x2c1b3c6d;
        value ^= value >> 12;
        value *= 0x297a2d39;
        value ^= value >> 15;
        return value;
    }
}