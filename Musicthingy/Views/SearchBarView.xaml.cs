namespace Musicthingy.Views;

public partial class SearchBarView : ContentView
{
    public static readonly BindableProperty QueryProperty = BindableProperty.Create(
        nameof(Query),
        typeof(string),
        typeof(SearchBarView),
        string.Empty,
        BindingMode.TwoWay);

    public static readonly BindableProperty PlaceholderProperty = BindableProperty.Create(
        nameof(Placeholder),
        typeof(string),
        typeof(SearchBarView),
        "Search",
        propertyChanged: (bindable, _, newValue) =>
            ((SearchBarView)bindable).ApplyPlaceholder((string?)newValue));

    public event EventHandler? SearchFocused;

    public string Query
    {
        get => (string?)GetValue(QueryProperty) ?? string.Empty;
        set => SetValue(QueryProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public SearchBarView()
    {
        InitializeComponent();
        QueryEntry.HandlerChanged += (_, _) => Dispatcher.Dispatch(ApplyPlainNativeEntry);
        QueryEntry.TextChanged += (_, _) => UpdatePlaceholderVisibility();
        Loaded += (_, _) => ApplyPlainNativeEntry();
        UpdatePlaceholderVisibility();
    }

    /// <summary>
    /// .NET MAUI 10 with Material 3 wraps Entry in an outlined TextInputLayout
    /// with a floating label. That draws a second border inside the pill and,
    /// at 44dp, clips the entered text. The search bar owns its visuals, so the
    /// native container is reduced to a plain editor (no box, no floating hint).
    /// </summary>
    private void ApplyPlainNativeEntry()
    {
#if ANDROID
        if (QueryEntry.Handler?.PlatformView is not Android.Views.View platformView)
            return;

        Google.Android.Material.TextField.TextInputLayout? inputLayout =
            platformView as Google.Android.Material.TextField.TextInputLayout
            ?? FindChild<Google.Android.Material.TextField.TextInputLayout>(platformView)
            ?? FindAncestor<Google.Android.Material.TextField.TextInputLayout>(platformView);

        Android.Widget.EditText? nativeEntry =
            platformView as Android.Widget.EditText
            ?? FindChild<Android.Widget.EditText>(platformView);

        if (inputLayout is not null)
        {
            // 0 == TextInputLayout.BOX_BACKGROUND_NONE.
            inputLayout.BoxBackgroundMode = 0;
            inputLayout.HintEnabled = false;
            inputLayout.SetMinimumHeight(0);
            inputLayout.SetPadding(0, 0, 0, 0);
        }

        if (nativeEntry is not null)
        {
            nativeEntry.SetBackgroundColor(Android.Graphics.Color.Transparent);
            nativeEntry.SetPadding(0, 0, 0, 0);
        }

        ApplyPlaceholder(Placeholder);
#endif
    }

    /// <summary>
    /// The pill draws its own placeholder label so it can follow the open
    /// playlist's name without depending on the native Material floating hint.
    /// </summary>
    private void ApplyPlaceholder(string? text)
    {
        if (PlaceholderLabel is null) return;

        PlaceholderLabel.Text = text ?? string.Empty;
        UpdatePlaceholderVisibility();
    }

    private void UpdatePlaceholderVisibility()
    {
        if (PlaceholderLabel is null) return;

        PlaceholderLabel.IsVisible = string.IsNullOrEmpty(QueryEntry.Text);
    }

#if ANDROID
    private static T? FindChild<T>(Android.Views.View? view) where T : Android.Views.View
    {
        if (view is null) return null;
        if (view is T match) return match;

        if (view is Android.Views.ViewGroup group)
        {
            for (int index = 0; index < group.ChildCount; index++)
            {
                T? found = FindChild<T>(group.GetChildAt(index));
                if (found is not null) return found;
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(Android.Views.View view) where T : Android.Views.View
    {
        Android.Views.View? current = view.Parent as Android.Views.View;
        while (current is not null && current is not T)
            current = current.Parent as Android.Views.View;

        return current as T;
    }
#endif

    /// <summary>
    /// Removes focus and hides the soft keyboard. Unfocus alone leaves the
    /// keyboard up on some Android IMEs, which used to cover the results menu
    /// after a result was activated or the menu was dismissed.
    /// </summary>
    public void Blur()
    {
        QueryEntry.Unfocus();

        if (QueryEntry.Handler is not null)
            _ = QueryEntry.HideSoftInputAsync(CancellationToken.None);
    }

    public void FocusEntry() => QueryEntry.Focus();

    private void OnBarTapped(object? sender, TappedEventArgs e) => QueryEntry.Focus();

    private void OnClearClicked(object? sender, EventArgs e)
    {
        Query = string.Empty;
        QueryEntry.Focus();
    }

    private void OnEntryFocused(object? sender, FocusEventArgs e)
    {
        // Material 3 re-applies the outlined TextInputLayout when the field
        // gains focus; strip it again so the pill stays a single surface.
        Dispatcher.Dispatch(ApplyPlainNativeEntry);
        SearchFocused?.Invoke(this, EventArgs.Empty);
    }
}
