namespace Musicthingy.Views;

#if DEBUG
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

/// <summary>
/// ROUND 13: debug-only performance HUD. The user asked for debug mode to
/// be usable; the redesign's constant per-frame costs were debug's lag, and
/// rounds 6-13 removed them - this overlay proves it live and pinpoints any
/// regression the moment it appears instead of leaving "it lags" as the
/// only symptom. It compiles away entirely in release (whole class is
/// #if DEBUG; the single call site in MobileMainPage is guarded too).
///
/// Shows, top-right, at ~4Hz:
///   FPS        - rolling frame rate from a 1fps-ticker sample window
///   UI ms      - rolling average main-thread hop latency (dispatch queue
///                pressure: high values = the UI thread is saturated)
///   StyleP     - LyricsView.ApplyDistanceStyles passes per second
///                (bounded at ~1 per line switch after round 13)
///   LyricScan  - SyncActiveLyric scans per second (16Hz while playing;
///                the "misses" suffix counts ticks that changed the active
///                line - the rest are no-ops by the round-13 guard)
///   Ticks      - playback position ticks per second reaching the VM
///
/// Counters are plain static volatiles - incrementing one is a handful of
/// ns and cannot perturb what it measures (the old diagnostics counters
/// were removed with the settings leak round; these live only in debug).
/// </summary>
public class DebugPerfHud : Label
{
    private const int WindowMs = 1000;

    private static volatile int _stylePasses;
    private static volatile int _lyricScans;
    private static volatile int _lyricScanMisses;
    private static volatile int _positionTicks;

    private int _frames;
    private double _uiHopMsTotal;
    private int _uiHops;
    private DateTime _windowStart = DateTime.UtcNow;
    private DateTime _lastFrameAt = DateTime.UtcNow;
    private double _fps;

    public static void CountStylePass() => _stylePasses++;
    public static void CountLyricScan() => _lyricScans++;
    public static void CountLyricScanHit() => _lyricScanMisses++;
    public static void CountPositionTick() => _positionTicks++;

    public DebugPerfHud()
    {
        Text = "FPS -";
        FontSize = 10;
        FontAttributes = FontAttributes.Bold;
        TextColor = Colors.Lime;
        BackgroundColor = Color.FromArgb("#B3000000");
        Padding = new Thickness(6, 3, 6, 3);
        HorizontalOptions = LayoutOptions.End;
        VerticalOptions = LayoutOptions.Start;
        InputTransparent = true;
        ZIndex = 999;

        // Frame-rate sampling: every UI-thread hop updates the rolling
        // frame counter. A device at 60fps hops ~16ms; when the UI thread
        // stalls, the hop gap grows and the FPS number falls accordingly.
        uint sampleMs = 16;
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(sampleMs);
        timer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;

            // Only count a hop as a frame when one was actually serviced
            // (the ticker itself rides the UI thread; a hop that arrives
            // late means the thread was busy and frames were dropped).
            double hopMs = (now - _lastFrameAt).TotalMilliseconds;
            _lastFrameAt = now;
            _uiHopMsTotal += Math.Min(hopMs, 250);
            _uiHops++;
            if (hopMs < sampleMs * 2.5)
                _frames++;

            if ((now - _windowStart).TotalMilliseconds >= WindowMs)
            {
                double seconds = Math.Max(0.05, (now - _windowStart).TotalSeconds);
                _fps = _frames / seconds;
                double avgHop = _uiHops > 0 ? _uiHopMsTotal / _uiHops : 0;

                Text =
                    $"FPS {_fps:F0}  UI {avgHop:F0}ms\n" +
                    $"StyleP {_stylePasses}  Scan {_lyricScans}/{_lyricScanMisses}h\n" +
                    $"Ticks {_positionTicks}";

                _frames = 0;
                _uiHopMsTotal = 0;
                _uiHops = 0;
                _stylePasses = 0;
                _lyricScans = 0;
                _lyricScanMisses = 0;
                _positionTicks = 0;
                _windowStart = now;
            }
        };
        timer.Start();
    }
}
#endif
