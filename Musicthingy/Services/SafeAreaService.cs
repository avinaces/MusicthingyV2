using Microsoft.Maui.ApplicationModel;

namespace Musicthingy.Services;

/// <summary>
/// The app's single source of truth for system-bar safe areas, following the
/// OpenTune window policy: the layout is edge-to-edge and the status /
/// gesture bars are TRANSPARENT, so only a surface color may paint under
/// them - no UI element is ever laid out inside those strips.
///
/// All values are reported in DENSITY-INDEPENDENT pixels (dp), the same unit
/// MAUI layouts use, so callers can use them directly as margins / paddings.
/// Must be read on the UI thread (insets come from the DecorView).
/// </summary>
public static class SafeAreaService
{
    // Cached dp values. Insets are only readable after the first layout pass,
    // so an early read that yields zeros is retried on the next call instead
    // of being cached forever; rotation clears the cache via Invalidate()
    // (MainActivity.OnConfigurationChanged).
    private static double _topDp;
    private static double _bottomDp;
    private static bool _measured;

    /// <summary>Height of the status-bar / notch strip at the top, in dp.</summary>
    public static double TopInset
    {
        get { EnsureMeasured(); return _topDp; }
    }

    /// <summary>Height of the gesture / button bar strip at the bottom, in dp.</summary>
    public static double BottomInset
    {
        get { EnsureMeasured(); return _bottomDp; }
    }

    /// <summary>
    /// Clears the cache so the next read picks up new insets
    /// (called after a rotation / configuration change).
    /// </summary>
    public static void Invalidate()
    {
        _measured = false;
    }

#if ANDROID
    private static void EnsureMeasured()
    {
        if (_measured)
            return;

        try
        {
            // Android.Views.Window has no Resources in the .NET binding;
            // the DecorView's Resources carries the same DisplayMetrics.
            Android.Views.View? decor = Platform.CurrentActivity?.Window?.DecorView;
            if (decor == null || decor.RootWindowInsets == null)
                return;

            // RootWindowInsets is only valid once the DecorView has been
            // laid out; before that GetInsets returns nothing useful and we
            // simply retry on a later call.
            AndroidX.Core.View.WindowInsetsCompat? compat =
                AndroidX.Core.View.ViewCompat.GetRootWindowInsets(decor);
            if (compat == null)
                return;

            int topPx = compat.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.StatusBars()).Top;
            int bottomPx = compat.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.NavigationBars()).Bottom;

            Android.Util.DisplayMetrics? metrics = decor.Resources?.DisplayMetrics;
            double density = metrics?.Density ?? 1.0;
            if (density <= 0) density = 1.0;

            _topDp = Math.Round(topPx / density, MidpointRounding.AwayFromZero);
            _bottomDp = Math.Round(bottomPx / density, MidpointRounding.AwayFromZero);

            // Real devices always report a nonzero status bar; only lock the
            // cache in once something was measured (0/0 means "too early").
            if (topPx > 0 || bottomPx > 0)
                _measured = true;
        }
        catch
        {
            // Inset APIs can throw in previews / emulators - treat as no inset.
        }
    }
#else
    private static void EnsureMeasured()
    {
        // No system-bar strips on the Windows desktop target.
        _measured = true;
    }
#endif
}

#if ANDROID
/// <summary>
/// One-shot window setup. It does NOT force edge-to-edge anymore: MAUI net10
/// already draws the window edge-to-edge on Android 15+ (and the AndroidX
/// SetDecorFitsSystemWindows(false) call in earlier rounds DE-synchronized
/// MAUI's automatic safe-area padding, which is what pushed the pane under
/// the bars and shrank the visible page). The theme XML keeps the bars
/// transparent; this class now only re-asserts the bar ICON appearance, so
/// the clock / gesture pills stay white over the dark background. Called
/// from MainActivity.OnCreate after the base call - idempotent.
///
/// Cache invalidation for rotation / foldables is handled by
/// MainActivity.OnConfigurationChanged calling SafeAreaService.Invalidate()
/// - a Java interface (IOnApplyWindowInsetsListener) cannot be created from
/// a C# lambda, so there is no inset-change listener here.
/// </summary>
internal static class SafeAreaSetup
{
    internal static void Apply(Android.App.Activity activity)
    {
        var window = activity.Window;
        if (window == null)
            return;

        // Bar icon APPEARANCE only (same guarantee ForceDarkSystemBars makes
        // at runtime): white status / gesture-bar icons over the dark app.
        // No SetDecorFitsSystemWindows, no bar-color writes - the theme XML
        // (Musicthingy.SplashTheme) already declares the transparent bars and
        // MAUI owns the inset handling from here.
        try
        {
            var controller = AndroidX.Core.View.WindowCompat.GetInsetsController(window, window.DecorView);
            if (controller != null)
            {
                controller.AppearanceLightStatusBars = false;
                controller.AppearanceLightNavigationBars = false;
            }
        }
        catch { }
    }
}
#endif
