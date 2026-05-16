using Microsoft.Maui.Controls;
using System;

namespace Musicthingy.Views;

public partial class SharedPopupContainer : ContentView
{
    public static readonly BindableProperty TitleProperty =
        BindableProperty.Create(nameof(Title), typeof(string), typeof(SharedPopupContainer), string.Empty);
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly BindableProperty SaveTextProperty =
        BindableProperty.Create(nameof(SaveText), typeof(string), typeof(SharedPopupContainer), "Save");
    public string SaveText { get => (string)GetValue(SaveTextProperty); set => SetValue(SaveTextProperty, value); }

    public static readonly BindableProperty DesktopWidthProperty =
        BindableProperty.Create(nameof(DesktopWidth), typeof(double), typeof(SharedPopupContainer), 450.0);
    public double DesktopWidth { get => (double)GetValue(DesktopWidthProperty); set => SetValue(DesktopWidthProperty, value); }

    public static readonly BindableProperty BodyProperty =
        BindableProperty.Create(nameof(Body), typeof(View), typeof(SharedPopupContainer));
    public View Body { get => (View)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    public event EventHandler? CancelClicked;
    public event EventHandler? SaveClicked;

    public SharedPopupContainer()
    {
        InitializeComponent();
    }

    private void OnCancelClicked(object sender, EventArgs e) => CancelClicked?.Invoke(this, e);
    private void OnSaveClicked(object sender, EventArgs e) => SaveClicked?.Invoke(this, e);
}