using System;
using System.Collections.Generic;
using Microsoft.Maui.Controls;
#if ANDROID
using Android.Graphics;
#endif

namespace Musicthingy.Effects;

/// <summary>
/// Direct, registration-free per-label blur (Android: GPU RenderEffect, API 31+).
///
/// Deliberately NOT a MAUI Effect: MAUI Effects must be registered via
/// builder.ConfigureEffects in MauiProgram.cs and resolve through the
/// EffectsFactory DI service - easy to get subtly wrong (a crash, or a
/// silent no-attach with zero diagnostics). This helper talks to the label's
/// platform view through its Handler instead: zero setup, and on platforms
/// without the API it is a no-op (the color/opacity depth fade in LyricsView
/// still conveys the distance).
///
/// Usage: LyricsBlurPlatform.SetRadius(label, radiusDp);
/// - The attached property dedupes unchanged values, so the native blur is
///   only re-applied when the radius truly changes (a few times per song).
/// - If the label has no platform view yet (freshly created by BindableLayout),
///   the value is remembered and applied as soon as its handler connects,
///   and re-applied if the handler is ever recreated.
/// </summary>
public static class LyricsBlurPlatform
{
    public static readonly BindableProperty RadiusProperty =
        BindableProperty.CreateAttached("Radius", typeof(double), typeof(LyricsBlurPlatform), 0.0,
            propertyChanged: OnRadiusChanged);

    // Cache of shared native blur effects, keyed by the rounded sigma in px.
    // Creating a RenderEffect is not free and the blur ladder only ever uses
    // a handful of distinct values, so every tier maps to ONE shared native
    // instance (created once, reused across all labels and restyles) instead
    // of a fresh allocation per label per change - hundreds of per-open
    // allocations were part of the lag spike when opening lyrics. Bounded
    // (one entry per tier); entries are never disposed before process exit.
#if ANDROID
    private static readonly Dictionary<int, RenderEffect> _effectCache = new();
#endif

    public static void SetRadius(BindableObject view, double radiusDp) => view.SetValue(RadiusProperty, radiusDp);

    public static double GetRadius(BindableObject view) => (double)view.GetValue(RadiusProperty);

    // Tracks labels whose handler was not ready yet, so HandlerChanged is
    // subscribed exactly once per label (ConditionalWeakTable: entries die
    // with their label, no leaks).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Label, object> _pendingHook =
        new();

    private static void OnRadiusChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not Label label)
            return;

        if (label.Handler != null)
        {
            Apply(label);
            return;
        }

        // Platform view not created yet - apply when it connects.
        if (!_pendingHook.TryGetValue(label, out _))
        {
            _pendingHook.Add(label, true);
            label.HandlerChanged += OnHandlerChanged;
        }
    }

    private static void OnHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is Label label)
            Apply(label);
    }

    private static void Apply(Label label)
    {
#if ANDROID
        if (label.Handler?.PlatformView is not global::Android.Views.View view)
            return;

        // RenderEffect requires Android 12 (API 31)
        if (!OperatingSystem.IsAndroidVersionAtLeast(31))
            return;

        double radiusDp = GetRadius(label);
        float density = view.Resources?.DisplayMetrics?.Density ?? 1f;

        // RenderEffect blur sigmas are in PIXELS - convert from dp, otherwise
        // a "14dp" cap arrives as 14px (~4dp on a 3.5x screen) and is invisible.
        float sigma = (float)(radiusDp * density);

        if (radiusDp > 0.01)
        {
            int sigmaPx = Math.Max(1, (int)Math.Round(sigma));
            if (!_effectCache.TryGetValue(sigmaPx, out RenderEffect? effect))
            {
                effect = RenderEffect.CreateBlurEffect(sigmaPx, sigmaPx, Shader.TileMode.Clamp);
                _effectCache[sigmaPx] = effect;
            }

            view.SetRenderEffect(effect);
        }
        else
        {
            view.SetRenderEffect(null);
        }

        System.Diagnostics.Debug.WriteLine(
            $"[LyricsBlur] applied {radiusDp:F1}dp -> sigma {sigma:F1}px on {view.GetType().Name}");
#else
        // Other platforms: no real blur available - the color/opacity depth
        // fade in LyricsView conveys the distance effect.
#endif
    }
}
