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
        // V1 Restored: Translate the parent wrapper exactly as SlideUpSheetHost expects.
        View sheetParent = this.Parent as View ?? this;

        switch (e.StatusType)
        {
            case GestureStatus.Running:
                // Only allow sliding downwards
                if (e.TotalY > 0)
                {
                    sheetParent.TranslationY = e.TotalY;

                    // Dim the dark background dynamically as you drag it down
                    OverlayBox.Opacity = Math.Clamp(1 - (e.TotalY / 400), 0, 1);
                }
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                // If dragged down more than 100px, close it
                if (sheetParent.TranslationY > 100)
                {
                    Close();
                }
                else
                {
                    // Otherwise, spring back into place
                    _ = sheetParent.TranslateToAsync(0, 0, 250, Easing.CubicOut);
                    _ = OverlayBox.FadeToAsync(1, 250, Easing.CubicOut);
                }
                break;
        }
    }
}