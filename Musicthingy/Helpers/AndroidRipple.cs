using System;
using Microsoft.Maui.Controls;

#if ANDROID
using Android.Util;
using Android.Views;
#endif

namespace Musicthingy.Helpers;

public static class AndroidRipple
{
    public static readonly BindableProperty AddRippleProperty =
        BindableProperty.CreateAttached(
            "AddRipple",
            typeof(bool),
            typeof(AndroidRipple),
            false,
            propertyChanged: OnAddRippleChanged);

    public static bool GetAddRipple(BindableObject view) => (bool)view.GetValue(AddRippleProperty);
    public static void SetAddRipple(BindableObject view, bool value) => view.SetValue(AddRippleProperty, value);

    private static void OnAddRippleChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is VisualElement visualElement)
        {
            if ((bool)newValue)
            {
                visualElement.HandlerChanged += OnHandlerChanged;
                if (visualElement.Handler != null)
                {
                    ApplyRipple(visualElement);
                }
            }
            else
            {
                visualElement.HandlerChanged -= OnHandlerChanged;
            }
        }
    }

    private static void OnHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is VisualElement visualElement)
        {
            ApplyRipple(visualElement);
        }
    }

    private static void ApplyRipple(VisualElement visualElement)
    {
#if ANDROID
        var platformView = visualElement.Handler?.PlatformView as Android.Views.View;
        if (platformView == null) return;

        var context = platformView.Context;
        if (context == null) return;

        // Ensure the native view is focusable and clickable so the Android OS handles the touch states
        platformView.Clickable = true;
        platformView.Focusable = true;

        using (var outValue = new TypedValue())
        {
            // Resolve Android's default borderless selectable item background (automatically maps to ColorControlHighlight)
            if (context.Theme != null && context.Theme.ResolveAttribute(Android.Resource.Attribute.SelectableItemBackground, outValue, true))
            {
                platformView.SetBackgroundResource(outValue.ResourceId);
            }
        }

        // Attach native Touch listener to handle hotspot updates
        platformView.Touch -= OnPlatformViewTouch;
        platformView.Touch += OnPlatformViewTouch;
#endif
    }

#if ANDROID
    private static void OnPlatformViewTouch(object? sender, Android.Views.View.TouchEventArgs e)
    {
        if (sender is Android.Views.View platformView && e.Event != null)
        {
            if (e.Event.Action == MotionEventActions.Down)
            {
                // Align the center of the native ripple exactly with your finger's coordinates.
                // We let Android natively transition Pressed state to prevent double-ripple stuttering.
                if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Lollipop)
                {
                    platformView.DrawableHotspotChanged(e.Event.GetX(), e.Event.GetY());
                }
            }
        }

        // Return false so that MAUI's TapGestureRecognizer still handles the tap event normally
        e.Handled = false;
    }
#endif
}