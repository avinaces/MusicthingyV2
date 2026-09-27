using System;
using System.Linq;
using Android.Graphics;
using Android.OS;
using Microsoft.Maui.Controls.Platform;
using Musicthingy.Effects;

[assembly: ResolutionGroupName("Musicthingy")]
[assembly: ExportEffect(typeof(Musicthingy.Platforms.Android.LyricsBlurPlatformEffect), nameof(LyricsBlurEffect))]

namespace Musicthingy.Platforms.Android;

/// <summary>
/// Applies a real gaussian blur to the lyric Label using Android's
/// GPU-accelerated RenderEffect (API 31+). SetRenderEffect is only called when
/// the radius actually changes - normally just a few times per song, each call
/// being a cheap GPU work submission. Below API 31 it is a no-op (the
/// color/opacity depth fade in LyricsView still conveys the distance effect).
///
/// NOTE: RenderEffect blur sigmas are in PIXELS. The effect Radius is
/// expressed in dp and converted with the display density here - without the
/// conversion a 14dp cap would arrive as 14px (about 4dp on a 3.5x screen)
/// and be effectively invisible.
///
/// Diagnostics: everything logs through Debug.WriteLine with the
/// "[LyricsBlur]" tag (stripped from release builds):
///   - attached/detached: proves the DI registration + attach path works
///   - applied: proves the native blur is actually being set
/// If logcat shows NO "attached" line, the effect never attached.
/// </summary>
public class LyricsBlurPlatformEffect : PlatformEffect
{
    private float _appliedRadiusDp = -1;
    private LyricsBlurEffect? _core;

    protected override void OnAttached()
    {
        // Read the core routing effect from the element (the shared effect
        // instance lives in Element.Effects). Radius may already be set here
        // if styling ran before the native view existed.
        _core = Element?.Effects.OfType<LyricsBlurEffect>().FirstOrDefault();
        if (_core != null)
            _core.RadiusChanged += OnRadiusChanged;

        System.Diagnostics.Debug.WriteLine(
            $"[LyricsBlur] attached: control={Control?.GetType().Name ?? "NULL"}, " +
            $"coreFound={_core != null}, coreRadius={_core?.Radius ?? 0}, " +
            $"api={Build.VERSION.SdkInt}, density={GetDensity():F2}");

        Apply();
    }

    private void OnRadiusChanged(object? sender, EventArgs e) => Apply();

    private float GetDensity()
    {
        var view = Control as global::Android.Views.View;
        return view?.Resources?.DisplayMetrics?.Density ?? 1f;
    }

    private void Apply()
    {
        if (Control is not global::Android.Views.View view)
        {
            System.Diagnostics.Debug.WriteLine("[LyricsBlur] apply skipped: Control is null/not a View");
            return;
        }

        // RenderEffect requires Android 12 (API 31)
        if (!OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsBlur] apply skipped: API {Build.VERSION.SdkInt} < 31");
            return;
        }

        float radiusDp = (float)(_core?.Radius ?? 0);
        if (Math.Abs(radiusDp - _appliedRadiusDp) < 0.01f)
            return; // unchanged - SetRenderEffect invalidates the view, skip redundant work

        _appliedRadiusDp = radiusDp;

        // Convert dp -> pixels for the native blur call (see class note).
        float sigma = radiusDp * GetDensity();

        view.SetRenderEffect(radiusDp > 0.01f
            ? RenderEffect.CreateBlurEffect(sigma, sigma, Shader.TileMode.Clamp)
            : null);

        System.Diagnostics.Debug.WriteLine($"[LyricsBlur] applied: {radiusDp:F1}dp -> sigma {sigma:F1}px");
    }

    protected override void OnDetached()
    {
        System.Diagnostics.Debug.WriteLine("[LyricsBlur] detached");

        if (_core != null)
        {
            _core.RadiusChanged -= OnRadiusChanged;
            _core = null;
        }

        if (Control is global::Android.Views.View view && OperatingSystem.IsAndroidVersionAtLeast(31))
            view.SetRenderEffect(null);

        _appliedRadiusDp = -1;
    }
}
