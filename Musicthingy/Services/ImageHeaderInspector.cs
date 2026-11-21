using System;

namespace Musicthingy.Services;

/// <summary>
/// Minimal JPEG size probe: reads the SOF marker dimensions without
/// decoding. Used by MediaResolutionService to reject img.youtube.com's
/// 120x90 gray placeholder (served instead of a 404 when a size variant
/// does not exist) without allocating a full bitmap.
/// </summary>
public static class ImageHeaderInspector
{
    /// <summary>Returns (width, height); (0, 0) if not a JPEG / no SOF found.</summary>
    public static (int Width, int Height) GetJpegSize(byte[] data)
    {
        if (data == null || data.Length < 4)
            return (0, 0);

        // SOI marker
        if (data[0] != 0xFF || data[1] != 0xD8)
            return (0, 0);

        int i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }

            byte marker = data[i + 1];

            // Standalone markers without a length field.
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD9) || marker == 0x01)
            {
                i += 2;
                continue;
            }

            int length = (data[i + 2] << 8) | data[i + 3];

            // SOF0..SOF15 except DHT/DAC/JPG families (C4, C8, CC).
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (i + 8 < data.Length)
                {
                    int height = (data[i + 5] << 8) | data[i + 6];
                    int width = (data[i + 7] << 8) | data[i + 8];
                    return (width, height);
                }
                return (0, 0);
            }

            i += 2 + length;
        }

        return (0, 0);
    }
}
