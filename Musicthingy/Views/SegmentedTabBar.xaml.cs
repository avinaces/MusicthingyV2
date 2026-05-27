using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Musicthingy.Views;

public partial class SegmentedTabBar : ContentView
{
    private const uint AnimationDurationMs = 250;

    private readonly List<Label> _labels = new List<Label>();
    private int _tabCount = 0;

    public event EventHandler<int>? SelectedIndexChanged;

    public static readonly BindableProperty TabsProperty =
        BindableProperty.Create(nameof(Tabs), typeof(string), typeof(SegmentedTabBar), string.Empty, propertyChanged: OnTabsChanged);

    public string Tabs
    {
        get => (string)GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    public static readonly BindableProperty SelectedIndexProperty =
        BindableProperty.Create(nameof(SelectedIndex), typeof(int), typeof(SegmentedTabBar), 0, propertyChanged: OnSelectedIndexChanged);

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public SegmentedTabBar()
    {
        InitializeComponent();
    }

    private static void OnTabsChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is SegmentedTabBar tabBar)
        {
            tabBar.BuildTabs();
        }
    }

    private static void OnSelectedIndexChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is SegmentedTabBar tabBar)
        {
            tabBar.UpdateSelection();
            tabBar.SelectedIndexChanged?.Invoke(tabBar, (int)newValue);
        }
    }

    private void BuildTabs()
    {
        MainGrid.Children.Clear();
        MainGrid.ColumnDefinitions.Clear();
        _labels.Clear();

        if (string.IsNullOrEmpty(Tabs))
        {
            _tabCount = 0;
            return;
        }

        string[] tabArray = Tabs.Split(',');
        List<string> tabList = tabArray.Select(t => t.Trim()).ToList();
        _tabCount = tabList.Count;

        for (int i = 0; i < _tabCount; i++)
        {
            MainGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            // Tap area container
            Grid tapGrid = new Grid { BackgroundColor = Colors.Transparent };
            TapGestureRecognizer tapGesture = new TapGestureRecognizer { CommandParameter = i };
            tapGesture.Tapped += OnTabTapped;
            tapGrid.GestureRecognizers.Add(tapGesture);

            // Tab Text Label
            Label label = new Label
            {
                Text = tabList[i],
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                FontAttributes = FontAttributes.Bold,
                FontSize = 14,
                TextColor = i == SelectedIndex ? ThemeResources.Color("OnSurfaceInverse") : ThemeResources.Color("MutedColor")
            };

            _labels.Add(label);
            tapGrid.Children.Add(label);

            Grid.SetColumn(tapGrid, i);
            MainGrid.Children.Add(tapGrid);
        }

        // Force the indicator to layout immediately if already sized
        UpdateIndicatorPosition(false);
    }

    private void OnTabTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter != null && int.TryParse(e.Parameter.ToString(), out int index))
        {
            SelectedIndex = index;
        }
    }

    private void UpdateSelection()
    {
        // Instantly transition text colors for immediate feedback
        for (int i = 0; i < _labels.Count; i++)
        {
            _labels[i].TextColor = i == SelectedIndex ? ThemeResources.Color("OnSurfaceInverse") : ThemeResources.Color("MutedColor");
        }

        // Smoothly slide the indicator background
        UpdateIndicatorPosition(true);
    }

    private void OnMainGridSizeChanged(object? sender, EventArgs e)
    {
        // Now we know MainGrid definitely has a width!
        UpdateIndicatorPosition(false);
    }

    private void UpdateIndicatorPosition(bool animate)
    {
        // Avoid calculating if layout hasn't been drawn yet
        if (_tabCount == 0 || MainGrid.Width <= 0) return;

        // Calculate the width of one tab evenly divided
        double tabWidth = MainGrid.Width / _tabCount;

        // Only update WidthRequest if it actually changed to prevent infinite layout loops
        if (Math.Abs(ActiveIndicator.WidthRequest - tabWidth) > 0.1)
        {
            ActiveIndicator.WidthRequest = tabWidth;
        }

        // Target X coordinate for the sliding animation
        double targetX = SelectedIndex * tabWidth;

        if (animate)
        {
            ActiveIndicator.TranslateToAsync(targetX, 0, AnimationDurationMs, Easing.CubicOut);
        }
        else
        {
            ActiveIndicator.CancelAnimations();
            ActiveIndicator.TranslationX = targetX;
        }
    }
}