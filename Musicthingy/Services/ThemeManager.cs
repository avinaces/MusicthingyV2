using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Musicthingy.Models;
using System.Collections.Generic;
using System.Diagnostics;

namespace Musicthingy.Services;

public class ThemeManager
{
    private readonly Dictionary<string, Color> _baseColors = new();

    private readonly string[] _themeableKeys = new string[]
    {
        // Surfaces
        "SurfaceCanvas", "SurfaceInput", "SurfacePanel",
        "SurfacePanelHover", "SurfaceRaised", "SurfaceSelected",
        "SurfaceSelectedStrong", "SurfaceScrim", "SurfaceScrimStrong", "SurfaceScrimPanel",
        "BgColor", "CardColor", "CardHoverColor", "TabIndicatorColor",
        
        // Accents
        "Accent", "AccentFill", "AccentFillHover", "AccentOutline",
        "AccentContainer", "Primary", "PrimaryDark", "Tertiary",
        "Magenta", "AccentColor", "SelectedBgColor", "SwipeEditColor",
        
        // Outlines and Typography that carry a tint
        "Outline", "InputOutline", "OutlineStrong", "DragHandleColor",
        "OnSurface", "OnSurfaceMuted", "OnSurfaceStrong", "TitleColor",
        "TextColor", "MutedColor", "BorderColor", "InputBorderColor",
        "TagBgColor", "TagTextColor", "IconSelectedColor",

        // Button Colors
        "PrimaryButtonColor", "PrimaryButtonText", "PrimaryButtonBorder",
        "SecondaryButtonColor", "SecondaryButtonText", "SecondaryButtonBorder"
    };

    private void CacheBaseColors()
    {
        if (Application.Current == null || _baseColors.Count > 0)
        {
            return;
        }

        ResourceDictionary resources = Application.Current.Resources;

        foreach (string key in _themeableKeys)
        {
            if (resources.TryGetValue(key, out object value) && value is Color color)
            {
                _baseColors[key] = color;
            }
        }
    }

    public void ApplyTheme(Color targetSeedColor)
    {
        if (Application.Current == null)
        {
            Debug.WriteLine("ThemeManager: No current application found. Cannot apply theme.");
            return;
        }

        CacheBaseColors();

        // Convert the input color to OKLCH just to extract the Hue
        Oklch targetOklch = Oklch.FromColor(targetSeedColor);
        float targetHue = targetOklch.H;

        ResourceDictionary resources = Application.Current.Resources;

        foreach (string key in _themeableKeys)
        {
            if (_baseColors.TryGetValue(key, out Color? baseColor))
            {
                Oklch baseColorOklch = Oklch.FromColor(baseColor);

                // 1. Keep the exact original Lightness for perfect contrast
                float l = baseColorOklch.L;

                // 2. Keep the exact original Chroma as defined in Colors.xaml
                float c = baseColorOklch.C;

                // 3. Only apply the user's new Hue. 
                // If a base color was totally grayscale (chroma effectively 0), we don't shift its hue.
                float h = baseColorOklch.C > 0.001f ? targetHue : baseColorOklch.H;

                Oklch themedOklch = new Oklch(l, c, h);
                resources[key] = themedOklch.ToColor();
            }
        }

        Debug.WriteLine($"ThemeManager: Applied new theme with Hue {targetHue}° from seed color {targetSeedColor}");
    }
}

public static class SystemThemeHelper
{
    public static Color? GetAndroidSystemAccentColor()
    {
#if ANDROID
        // Material You dynamic colors are only available on Android 12 (API 31) and higher
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.S)
        {
            var context = Android.App.Application.Context;

            // "SystemAccent1500" is the standard vibrant primary color generated from the wallpaper
            int colorResId = Android.Resource.Color.SystemAccent1500;
            var androidColor = new Android.Graphics.Color(context.Resources.GetColor(colorResId, context.Theme));

            return Color.FromRgba(androidColor.R, androidColor.G, androidColor.B, androidColor.A);
        }
#endif
        // Returns null on Windows or older Android versions
        return null;
    }
}