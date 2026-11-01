using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;

namespace Musicthingy.Views;

public class AutoMarqueeLabel : ContentView
{
    private readonly AbsoluteLayout _canvas;
    private readonly Label _label1;
    private readonly Label _label2;

    private double _textWidth;
    private double _cycleWidth;

    private double _lastWidth;
    private double _lastHeight;

    private const double ScrollSpeed = 35.0;
    private const double GapWidth = 60.0;
    private const uint PauseDurationMs = 3500u;

    // rate: 16 = ~60fps | rate: 8 = ~120fps
    private const uint AnimationFrameRateMs = 16u;

    public static readonly BindableProperty TextProperty =
        BindableProperty.Create(nameof(Text), typeof(string), typeof(AutoMarqueeLabel), string.Empty,
            propertyChanged: (b, o, n) => ((AutoMarqueeLabel)b).Restart());

    public static readonly BindableProperty TextColorProperty =
        BindableProperty.Create(nameof(TextColor), typeof(Color), typeof(AutoMarqueeLabel), Colors.White,
            propertyChanged: (b, o, n) => ((AutoMarqueeLabel)b).ApplyStyles());

    public static readonly BindableProperty FontSizeProperty =
        BindableProperty.Create(nameof(FontSize), typeof(double), typeof(AutoMarqueeLabel), 14.0,
            propertyChanged: (b, o, n) => ((AutoMarqueeLabel)b).Restart());

    public static readonly BindableProperty FontAttributesProperty =
        BindableProperty.Create(nameof(FontAttributes), typeof(FontAttributes), typeof(AutoMarqueeLabel), FontAttributes.None,
            propertyChanged: (b, o, n) => ((AutoMarqueeLabel)b).Restart());

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Color TextColor
    {
        get => (Color)GetValue(TextColorProperty);
        set => SetValue(TextColorProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontAttributes FontAttributes
    {
        get => (FontAttributes)GetValue(FontAttributesProperty);
        set => SetValue(FontAttributesProperty, value);
    }

    public AutoMarqueeLabel()
    {
        _label1 = CreateLabel();
        _label2 = CreateLabel();

        _canvas = new AbsoluteLayout();

        AbsoluteLayout.SetLayoutBounds(_label1, new Rect(0, 0, -1, -1));
        AbsoluteLayout.SetLayoutFlags(_label1, AbsoluteLayoutFlags.None);

        AbsoluteLayout.SetLayoutBounds(_label2, new Rect(0, 0, -1, -1));
        AbsoluteLayout.SetLayoutFlags(_label2, AbsoluteLayoutFlags.None);

        _canvas.Children.Add(_label1);
        _canvas.Children.Add(_label2);

        // Grid's native clipping is heavily optimized compared to calculating custom Geometry clips
        var viewport = new Grid
        {
            IsClippedToBounds = true,
            Children = { _canvas }
        };

        Content = viewport;

        // Use Loaded/Unloaded to save battery when the user navigates away
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, EventArgs e) => Restart();

    private void OnUnloaded(object? sender, EventArgs e) => this.AbortAnimation("Marquee");

    // Replaces SizeChanged event - safer for memory management and avoids delegate allocations
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        if (Math.Abs(_lastWidth - width) < 0.5 && Math.Abs(_lastHeight - height) < 0.5)
            return;

        _lastWidth = width;
        _lastHeight = height;

        Restart();
    }

    private Label CreateLabel()
    {
        return new Label
        {
            LineBreakMode = LineBreakMode.NoWrap,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Start,
            VerticalTextAlignment = TextAlignment.Center
        };
    }

    private void ApplyStyles()
    {
        _label1.TextColor = TextColor;
        _label1.FontSize = FontSize;
        _label1.FontAttributes = FontAttributes;

        _label2.TextColor = TextColor;
        _label2.FontSize = FontSize;
        _label2.FontAttributes = FontAttributes;
    }

    private void Restart()
    {
        this.AbortAnimation("Marquee");

        ApplyStyles();

        var text = Text ?? string.Empty;

        _label1.Text = text;
        _label2.Text = text;

        if (string.IsNullOrWhiteSpace(text) || Width <= 0)
        {
            _label2.IsVisible = false;
            return;
        }

        var measure = _label1.Measure(double.PositiveInfinity, double.PositiveInfinity);
        _textWidth = measure.Width;

        // Text fits, disable marquee completely
        if (_textWidth <= Width)
        {
            _label2.IsVisible = false;
            AbsoluteLayout.SetLayoutBounds(_label1, new Rect(0, 0, _textWidth, Height));
            _canvas.TranslationX = 0;
            return;
        }

        _label2.IsVisible = true;
        _cycleWidth = _textWidth + GapWidth;

        AbsoluteLayout.SetLayoutBounds(_label1, new Rect(0, 0, _textWidth, Height));
        AbsoluteLayout.SetLayoutBounds(_label2, new Rect(_cycleWidth, 0, _textWidth, Height));

        _canvas.TranslationX = 0;

        StartAnimation();
    }

    private void StartAnimation()
    {
        double distance1 = _textWidth - Width;
        double distance2 = Width + GapWidth;

        double duration1 = (distance1 / ScrollSpeed) * 1000.0;
        double duration2 = (distance2 / ScrollSpeed) * 1000.0;

        double totalDuration = duration1 + duration2 + PauseDurationMs * 2;

        double p1 = PauseDurationMs / totalDuration;
        double p2 = (PauseDurationMs + duration1) / totalDuration;
        double p3 = (PauseDurationMs * 2 + duration1) / totalDuration;

        var animation = new Animation();

        animation.Add(0.0, p1, new Animation(v => _canvas.TranslationX = v, 0, 0));
        animation.Add(p1, p2, new Animation(v => _canvas.TranslationX = v, 0, -distance1));
        animation.Add(p2, p3, new Animation(v => _canvas.TranslationX = v, -distance1, -distance1));
        animation.Add(p3, 1.0, new Animation(v => _canvas.TranslationX = v, -distance1, -_cycleWidth));

        animation.Commit(
            owner: this,
            name: "Marquee",
            rate: AnimationFrameRateMs, // Using 8ms for 120Hz support
            length: (uint)totalDuration,
            easing: Easing.Linear,
            repeat: () =>
            {
                _canvas.TranslationX = 0;
                return true;
            });
    }

    // Forgiving right-edge slack for the fitted-text case: the measured text
    // advance and the actual glyph rendering can differ by a pixel or two.
    private const double TextHitTolerance = 8.0;

    /// <summary>
    /// True when <paramref name="point"/> - given in this control's own
    /// coordinate space - lands on the rendered TEXT rather than the empty
    /// rest of the control. Used by the player pane so clicking the title
    /// text acts as "scroll to track" while clicks on the empty strip (or
    /// the artist line below) do nothing. Marquee-aware: while the text is
    /// scrolling, the test follows the two sweeping text copies at their
    /// current canvas offset, so only points actually over text count.
    /// </summary>
    public bool HitTestText(Point point)
    {
        if (double.IsNaN(Width) || Width <= 0 || Height <= 0)
            return false;

        if (point.X < 0 || point.X > Width || point.Y < 0 || point.Y > Height)
            return false;

        var text = Text;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Text fits inside the control: only the glyph run counts.
        // Restart() parks the label frame at X=0 in that case.
        if (_textWidth <= Width)
            return point.X <= _textWidth + TextHitTolerance;

        // Marquee active: copy 1 occupies [TranslationX, TranslationX+width]
        // and copy 2 sits one cycle further right; both sweep the strip.
        double t = _canvas.TranslationX;
        double start1 = t;
        double end1 = t + _textWidth;
        double start2 = t + _cycleWidth;
        double end2 = start2 + _textWidth;

        return (point.X >= start1 && point.X <= end1) ||
               (point.X >= start2 && point.X <= end2);
    }
}
