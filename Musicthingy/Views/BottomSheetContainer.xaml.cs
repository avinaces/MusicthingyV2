using Microsoft.Maui.Controls;
using System;

namespace Musicthingy.Views;

public partial class BottomSheetContainer : ContentView
{
    public static readonly BindableProperty TitleProperty = BindableProperty.Create(nameof(Title), typeof(string), typeof(BottomSheetContainer));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly BindableProperty BodyProperty = BindableProperty.Create(nameof(Body), typeof(View), typeof(BottomSheetContainer));
    public View Body { get => (View)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    public static readonly BindableProperty FooterProperty = BindableProperty.Create(nameof(Footer), typeof(View), typeof(BottomSheetContainer), propertyChanged: OnFooterChanged);
    public View Footer { get => (View)GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    public bool HasFooter => Footer != null;

    public event EventHandler? CloseRequested;

    private double _velocityY;
    private double _lastPanY;
    private DateTime _lastPanTime;

    private const float BACKDROP_OPACITY = 0.3f;

    private static void OnFooterChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is BottomSheetContainer container)
            container.OnPropertyChanged(nameof(HasFooter));
    }

    public BottomSheetContainer()
    {
        InitializeComponent();
    }

    protected override void OnParentSet()
    {
        base.OnParentSet();
        if (Parent != null)
        {
            OverlayBox.Opacity = 0;
            _ = OverlayBox.FadeToAsync(BACKDROP_OPACITY, 250, Easing.CubicOut);
        }
    }

    public void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOverlayTapped(object? sender, TappedEventArgs e)
    {
        Close();
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        View sheetParent = this.Parent as View ?? this;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _velocityY = 0;
                _lastPanY = 0;
                _lastPanTime = DateTime.UtcNow;
                break;

            case GestureStatus.Running:
                var now = DateTime.UtcNow;
                var dt = (now - _lastPanTime).TotalMilliseconds;
                if (dt > 0)
                {
                    double currentV = (e.TotalY - _lastPanY) / dt;
                    _velocityY = (_velocityY == 0) ? currentV : (_velocityY * 0.2 + currentV * 0.8);
                }
                _lastPanY = e.TotalY;
                _lastPanTime = now;

                if (e.TotalY > 0)
                {
                    sheetParent.TranslationY = e.TotalY;

                    // (Dividing by 800 makes the fade-out smoother while dragging)
                    OverlayBox.Opacity = Math.Clamp(BACKDROP_OPACITY - (e.TotalY / 800), 0, BACKDROP_OPACITY);
                }
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (sheetParent.TranslationY > 100 || (_velocityY > 1.0 && sheetParent.TranslationY > 40))
                {
                    Close();
                }
                else
                {
                    uint duration = 250;
                    if (_velocityY < -0.1)
                    {
                        duration = (uint)Math.Clamp(Math.Abs(sheetParent.TranslationY / _velocityY), 100, 350);
                    }

                    _ = sheetParent.TranslateToAsync(0, 0, duration, Easing.CubicOut);

                    _ = OverlayBox.FadeToAsync(BACKDROP_OPACITY, duration, Easing.CubicOut);
                }
                break;
        }
    }
}