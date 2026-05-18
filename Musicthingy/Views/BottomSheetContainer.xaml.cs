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
            // Smoothly fade the overlay in. Doesn't conflict with SlideUpSheetHost translating the wrapper.
            OverlayBox.Opacity = 0;
            _ = OverlayBox.FadeToAsync(1, 250, Easing.CubicOut);
        }
    }

    /// <summary>
    /// Invokes the CloseRequested event to signal the host to natively close the bottom sheet.
    /// </summary>
    public void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOverlayTapped(object? sender, TappedEventArgs e)
    {
        // Simply requesting to close lets SlideUpSheetHost handle its clean, native exit animation.
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
                // Calculate velocity (pixels per millisecond)
                var now = DateTime.UtcNow;
                var dt = (now - _lastPanTime).TotalMilliseconds;
                if (dt > 0)
                {
                    double currentV = (e.TotalY - _lastPanY) / dt;
                    // Smooth out the velocity reading
                    _velocityY = (_velocityY == 0) ? currentV : (_velocityY * 0.2 + currentV * 0.8);
                }
                _lastPanY = e.TotalY;
                _lastPanTime = now;

                // Only allow sliding downwards
                if (e.TotalY > 0)
                {
                    sheetParent.TranslationY = e.TotalY;
                    OverlayBox.Opacity = Math.Clamp(1 - (e.TotalY / 400), 0, 1);
                }
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                // Close if dragged down more than 100px OR flicked downwards quickly (with at least 40px of movement)
                if (sheetParent.TranslationY > 100 || (_velocityY > 1.0 && sheetParent.TranslationY > 40))
                {
                    Close();
                }
                else
                {
                    // Dynamic spring back based on how fast they flicked UP (negative velocity)
                    uint duration = 250;
                    if (_velocityY < -0.1)
                    {
                        duration = (uint)Math.Clamp(Math.Abs(sheetParent.TranslationY / _velocityY), 100, 350);
                    }

                    _ = sheetParent.TranslateToAsync(0, 0, duration, Easing.CubicOut);
                    _ = OverlayBox.FadeToAsync(1, duration, Easing.CubicOut);
                }
                break;
        }
    }
}