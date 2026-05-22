using Microsoft.Maui.Graphics;
using System;

namespace Musicthingy.Models;

public struct Oklab
{
    public float L;
    public float A;
    public float B;

    public Oklab(float l, float a, float b)
    {
        L = l;
        A = a;
        B = b;
    }

    // Constants and matrices are derived directly from the official OKLAB specification (Björn Ottosson, 2020)
    public Color ToColor()
    {
        float l_ = L + 0.3963377774f * A + 0.2158037573f * B;
        float m_ = L - 0.1055613458f * A - 0.0638541728f * B;
        float s_ = L - 0.0894841775f * A - 1.2914855480f * B;

        float l = l_ * l_ * l_;
        float m = m_ * m_ * m_;
        float s = s_ * s_ * s_;

        float rLinear = +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        float gLinear = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        float bLinear = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;

        float r = GammaCorrect(rLinear);
        float g = GammaCorrect(gLinear);
        float bComponent = GammaCorrect(bLinear);

        r = Math.Clamp(r, 0f, 1f);
        g = Math.Clamp(g, 0f, 1f);
        bComponent = Math.Clamp(bComponent, 0f, 1f);

        return new Color(r, g, bComponent);
    }

    public static Oklab FromColor(Color color)
    {
        float rLinear = InverseGammaCorrect(color.Red);
        float gLinear = InverseGammaCorrect(color.Green);
        float bLinear = InverseGammaCorrect(color.Blue);

        float l = 0.4122214708f * rLinear + 0.5363325363f * gLinear + 0.0514459929f * bLinear;
        float m = 0.2119034982f * rLinear + 0.6806995451f * gLinear + 0.1073969566f * bLinear;
        float s = 0.0883024619f * rLinear + 0.2817188376f * gLinear + 0.6299787005f * bLinear;

        float l_ = MathF.Cbrt(l);
        float m_ = MathF.Cbrt(m);
        float s_ = MathF.Cbrt(s);

        float L = 0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_;
        float a = 1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_;
        float b = 0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_;

        return new Oklab(L, a, b);
    }

    private static float GammaCorrect(float colorChannel)
    {
        if (colorChannel >= 0.0031308f)
        {
            return 1.055f * MathF.Pow(colorChannel, 1.0f / 2.4f) - 0.055f;
        }

        return 12.92f * colorChannel;
    }

    private static float InverseGammaCorrect(float colorChannel)
    {
        if (colorChannel >= 0.04045f)
        {
            return MathF.Pow((colorChannel + 0.055f) / 1.055f, 2.4f);
        }

        return colorChannel / 12.92f;
    }
}

public struct Oklch
{
    public float L;
    public float C;
    public float H;

    public Oklch(float l, float c, float h)
    {
        L = l;
        C = c;
        H = h;
    }

    public Oklab ToOklab()
    {
        float hueRadians = H * MathF.PI / 180f;
        float a = C * MathF.Cos(hueRadians);
        float b = C * MathF.Sin(hueRadians);

        return new Oklab(L, a, b);
    }

    // NEW GAMUT-MAPPED ToColor()
    public Color ToColor()
    {
        // 1. Try to get the linear RGB with full chroma
        GetLinearRgb(C, out float r, out float g, out float b);

        // 2. If it fits perfectly inside standard monitor limits, just return it
        if (r >= 0f && r <= 1f && g >= 0f && g <= 1f && b >= 0f && b <= 1f)
        {
            return new Color(GammaCorrect(r), GammaCorrect(g), GammaCorrect(b));
        }

        // 3. GAMUT MAPPING: If it's physically impossible to display, we binary-search 
        // to find the absolute maximum Chroma that will fit without distorting the Hue or Lightness.
        float minC = 0f;
        float maxC = C;
        float currentC = 0f;

        for (int i = 0; i < 15; i++) // 15 steps is plenty of precision
        {
            currentC = (minC + maxC) / 2f;
            GetLinearRgb(currentC, out r, out g, out b);

            if (r >= 0f && r <= 1f && g >= 0f && g <= 1f && b >= 0f && b <= 1f)
                minC = currentC; // It fits! Try adding more chroma
            else
                maxC = currentC; // Out of bounds, reduce chroma
        }

        // Calculate the final safe RGB values
        GetLinearRgb(minC, out r, out g, out b);

        // Apply gamma correction (and a tiny safety clamp for floating-point rounding errors)
        return new Color(
            Math.Clamp(GammaCorrect(r), 0f, 1f),
            Math.Clamp(GammaCorrect(g), 0f, 1f),
            Math.Clamp(GammaCorrect(b), 0f, 1f)
        );
    }

    private void GetLinearRgb(float chroma, out float r, out float g, out float b)
    {
        float hueRadians = H * MathF.PI / 180f;
        float a = chroma * MathF.Cos(hueRadians);
        float b_ok = chroma * MathF.Sin(hueRadians);

        // Oklab to Linear RGB matrices
        float l_ = L + 0.3963377774f * a + 0.2158037573f * b_ok;
        float m_ = L - 0.1055613458f * a - 0.0638541728f * b_ok;
        float s_ = L - 0.0894841775f * a - 1.2914855480f * b_ok;

        float l3 = l_ * l_ * l_;
        float m3 = m_ * m_ * m_;
        float s3 = s_ * s_ * s_;

        r = +4.0767416621f * l3 - 3.3077115913f * m3 + 0.2309699292f * s3;
        g = -1.2684380046f * l3 + 2.6097574011f * m3 - 0.3413193965f * s3;
        b = -0.0041960863f * l3 - 0.7034186147f * m3 + 1.7076147010f * s3;
    }

    private static float GammaCorrect(float colorChannel)
    {
        if (colorChannel >= 0.0031308f)
            return 1.055f * MathF.Pow(colorChannel, 1.0f / 2.4f) - 0.055f;

        return 12.92f * colorChannel;
    }

    public static Oklch FromOklab(Oklab oklab)
    {
        float chroma = MathF.Sqrt(oklab.A * oklab.A + oklab.B * oklab.B);
        float hue = MathF.Atan2(oklab.B, oklab.A) * 180f / MathF.PI;

        if (hue < 0f) hue += 360f;

        return new Oklch(oklab.L, chroma, hue);
    }

    public static Oklch FromColor(Color color)
    {
        Oklab oklab = Oklab.FromColor(color);
        return FromOklab(oklab);
    }
}