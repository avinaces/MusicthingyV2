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

    public BottomSheetContainer() { InitializeComponent(); }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        // Translate the parent wrapper (the active sheet) so SlideUpSheetHost handles the exit animation seamlessly
        var sheetParent = this.Parent as View ?? this;

        switch (e.StatusType)
        {
            case GestureStatus.Running:
                // Only allow sliding downwards
                if (e.TotalY > 0)
                {
                    sheetParent.TranslationY = e.TotalY;
                }
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                // If dragged down more than 100px, close it
                if (sheetParent.TranslationY > 100)
                {
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    // Otherwise, spring back into place
                    sheetParent.TranslateTo(0, 0, 250, Easing.CubicOut);
                }
                break;
        }
    }
}