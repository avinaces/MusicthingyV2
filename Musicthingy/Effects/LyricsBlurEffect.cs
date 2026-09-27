using System;
using Microsoft.Maui.Controls;

namespace Musicthingy.Effects;

/// <summary>
/// Cross-platform routing effect that applies a real gaussian blur to a
/// VisualElement (a lyric line). The Radius can be updated at any time -
/// the platform implementation only re-applies the native blur when the
/// value actually changes, so switching the highlighted lyric line costs
/// a couple of GPU RenderEffect updates, not a per-frame workload.
///
/// Android: GPU-accelerated RenderEffect (API 31+), no-op fallback below.
/// Other platforms: no-op (the color/opacity depth fade still applies).
///
/// Registration: the platform effect is exported as
/// [ResolutionGroupName("Musicthingy")] + ExportEffect(..., "LyricsBlurEffect"),
/// which matches the resolution id passed to the base constructor.
/// </summary>
public class LyricsBlurEffect : RoutingEffect
{
    double _radius;

    public LyricsBlurEffect() : base("Musicthingy.LyricsBlurEffect")
    {
    }

    /// <summary>Blur radius in device-independent pixels. 0 = no blur.</summary>
    public double Radius
    {
        get => _radius;
        set
        {
            if (Math.Abs(_radius - value) < 0.01)
                return; // unchanged - skip the native round-trip entirely

            _radius = value;
            RadiusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? RadiusChanged;
}
