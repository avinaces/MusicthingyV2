using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;

namespace Musicthingy.Views;

public class AutoMarqueeLabel : ContentView
{
    private readonly Grid _viewport;

    private readonly Label _label1;
    private readonly Label _label2;

    private readonly AbsoluteLayout _canvas;

    private readonly RectangleGeometry _clip = new();

    private double _textWidth;
    private double _cycleWidth;

    private double _lastWidth;
    private double _lastHeight;

    private const double ScrollSpeed = 40.0;
    private const double GapWidth = 75.0;
    private const uint PauseDurationMs = 1200u;

    public static readonly BindableProperty TextProperty =
        BindableProperty.Create(
            nameof(Text),
            typeof(string),
            typeof(AutoMarqueeLabel),
            string.Empty,
            propertyChanged: (b, o, n) =>
            {
                ((AutoMarqueeLabel)b).Restart();
            });

    public static readonly BindableProperty TextColorProperty =
        BindableProperty.Create(
            nameof(TextColor),
            typeof(Color),
            typeof(AutoMarqueeLabel),
            Colors.White,
            propertyChanged: (b, o, n) =>
            {
                ((AutoMarqueeLabel)b).ApplyStyles();
            });

    public static readonly BindableProperty FontSizeProperty =
        BindableProperty.Create(
            nameof(FontSize),
            typeof(double),
            typeof(AutoMarqueeLabel),
            14.0,
            propertyChanged: (b, o, n) =>
            {
                ((AutoMarqueeLabel)b).Restart();
            });

    public static readonly BindableProperty FontAttributesProperty =
        BindableProperty.Create(
            nameof(FontAttributes),
            typeof(FontAttributes),
            typeof(AutoMarqueeLabel),
            FontAttributes.None,
            propertyChanged: (b, o, n) =>
            {
                ((AutoMarqueeLabel)b).Restart();
            });

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

        _viewport = new Grid
        {
            Clip = _clip
        };

        _viewport.Children.Add(_canvas);

        Content = _viewport;

        SizeChanged += OnSizeChanged;
        Loaded += (_, _) => Restart();
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        if (Math.Abs(_lastWidth - Width) < 0.5 &&
            Math.Abs(_lastHeight - Height) < 0.5)
        {
            return;
        }

        _lastWidth = Width;
        _lastHeight = Height;

        UpdateClip();

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
        foreach (var label in new[] { _label1, _label2 })
        {
            label.TextColor = TextColor;
            label.FontSize = FontSize;
            label.FontAttributes = FontAttributes;
        }
    }

    private void UpdateClip()
    {
        if (Width <= 0 || Height <= 0)
            return;

        _clip.Rect = new Rect(0, 0, Width, Height);
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

        var measure = _label1.Measure(
            double.PositiveInfinity,
            double.PositiveInfinity);

        _textWidth = measure.Width;

        // Text fits, disable marquee completely
        if (_textWidth <= Width)
        {
            _label2.IsVisible = false;

            AbsoluteLayout.SetLayoutBounds(
                _label1,
                new Rect(0, 0, _textWidth, Height));

            _canvas.TranslationX = 0;

            return;
        }

        _label2.IsVisible = true;

        _cycleWidth = _textWidth + GapWidth;

        AbsoluteLayout.SetLayoutBounds(
            _label1,
            new Rect(0, 0, _textWidth, Height));

        AbsoluteLayout.SetLayoutBounds(
            _label2,
            new Rect(_cycleWidth, 0, _textWidth, Height));

        _canvas.TranslationX = 0;

        StartAnimation();
    }

    private void StartAnimation()
    {
        uint scrollDuration =
            (uint)((_cycleWidth / ScrollSpeed) * 1000.0);

        var animation = new Animation();

        // Pause at start
        animation.Add(
            0.0,
            0.15,
            new Animation(v =>
            {
                _canvas.TranslationX = v;
            }, 0, 0));

        // Scroll
        animation.Add(
            0.15,
            0.85,
            new Animation(v =>
            {
                _canvas.TranslationX = v;
            }, 0, -_cycleWidth));

        // Pause at end
        animation.Add(
            0.85,
            1.0,
            new Animation(v =>
            {
                _canvas.TranslationX = v;
            }, -_cycleWidth, -_cycleWidth));

        animation.Commit(
            this,
            "Marquee",
            rate: 16,
            length: scrollDuration + PauseDurationMs * 2u,
            easing: Easing.Linear,
            repeat: () =>
            {
                _canvas.TranslationX = 0;
                return true;
            });
    }

    protected override void OnParentSet()
    {
        base.OnParentSet();

        if (Parent == null)
        {
            this.AbortAnimation("Marquee");
        }
    }
}