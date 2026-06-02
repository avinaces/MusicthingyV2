using Microsoft.Maui.Controls;
using System;
using System.Threading.Tasks;
using Musicthingy.Helpers;

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

    private readonly PanVelocityTracker _velocityTracker = new();

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
                _velocityTracker.Start();
                break;

            case GestureStatus.Running:
                _velocityTracker.Update(0, e.TotalY);

                if (e.TotalY > 0)
                {
                    sheetParent.TranslationY = e.TotalY;

                    // (Dividing by 800 makes the fade-out smoother while dragging)
                    OverlayBox.Opacity = Math.Clamp(BACKDROP_OPACITY - (e.TotalY / 800), 0, BACKDROP_OPACITY);
                }
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (sheetParent.TranslationY > 100 || (_velocityTracker.VelocityY > 1.0 && sheetParent.TranslationY > 40))
                {
                    _ = CloseWithVelocityAsync(_velocityTracker.VelocityY, sheetParent);
                }
                else
                {
                    uint duration = 250;
                    if (_velocityTracker.VelocityY < -0.1)
                    {
                        duration = (uint)Math.Clamp(Math.Abs(sheetParent.TranslationY / _velocityTracker.VelocityY), 100, 350);
                    }

                    _ = sheetParent.TranslateToAsync(0, 0, duration, Easing.CubicOut);
                    _ = OverlayBox.FadeToAsync(BACKDROP_OPACITY, duration, Easing.CubicOut);
                }
                break;
        }
    }

    private async Task CloseWithVelocityAsync(double velocity, View sheetParent)
    {
        uint duration = 250;

        // Target exactly the height of the sheet (with a fallback just in case), 
        // so it doesn't calculate a massive speed just to reach 1500px off-screen.
        double targetTranslation = sheetParent.Height > 0 ? sheetParent.Height : 800;
        double distance = targetTranslation - sheetParent.TranslationY;

        if (velocity > 0.1)
            duration = (uint)Math.Clamp(distance / velocity, 100, 350);


        _ = OverlayBox.FadeToAsync(0, duration, Easing.CubicOut);
        await sheetParent.TranslateToAsync(0, targetTranslation, duration, Easing.CubicOut);

        Close();
    }
}