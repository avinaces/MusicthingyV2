using CommunityToolkit.Maui.Views;
using Musicthingy.Settings;

namespace Musicthingy.Views.Dialogs;

public partial class ColorPickerDialog : Popup
{
    private readonly ISetting _setting;
    private readonly HueWheelDrawable _drawable;
    private bool _isUpdating = false;

    public ColorPickerDialog(ISetting setting)
    {
        InitializeComponent();
        _setting = setting;
        BindingContext = setting;

        _drawable = new HueWheelDrawable();
        HueWheelView.Drawable = _drawable;

        // Load initial color
        if (!string.IsNullOrWhiteSpace(setting.DisplayValue))
        {
            try
            {
                var color = Color.FromArgb(setting.DisplayValue);
                SetColor(color);
            }
            catch
            {
                SetColor(Colors.Red);
            }
        }
        else
        {
            SetColor(Colors.Red);
        }

        // Force a redraw after the popup is laid out — fixes the issue where
        // the color wheel doesn't render until you interact with it.
        this.SizeChanged += (s, e) => HueWheelView.Invalidate();
    }

    private void SetColor(Color color)
    {
        if (_isUpdating) return;
        _isUpdating = true;

        // Extract just the Hue (0.0 to 1.0 in MAUI)
        float hue = color.GetHue();

        // Enforce maximum saturation (1) and normal lightness (0.5) for pure hue
        var pureHueColor = Color.FromHsla(hue, 1f, 0.5f);

        UpdateDrawable(hue);

        ColorPreview.BackgroundColor = pureHueColor;
        HexInput.Text = ColorToHex(pureHueColor);

        _isUpdating = false;
    }

    private void OnWheelInteraction(object sender, TouchEventArgs e)
    {
        if (_isUpdating) return;
        UpdateFromTouch(e.Touches[0]);
    }

    private void UpdateFromTouch(PointF touch)
    {
        float cx = 100f; // WidthRequest / 2
        float cy = 100f; // HeightRequest / 2

        float dx = touch.X - cx;
        float dy = touch.Y - cy;

        // Calculate the angle based on the touch position
        float angle = (float)Math.Atan2(dy, dx);
        if (angle < 0) angle += 2f * (float)Math.PI;

        float hue = angle / (2f * (float)Math.PI);

        _isUpdating = true;

        var pureHueColor = Color.FromHsla(hue, 1f, 0.5f);

        UpdateDrawable(hue);

        ColorPreview.BackgroundColor = pureHueColor;
        HexInput.Text = ColorToHex(pureHueColor);

        _isUpdating = false;
    }

    private void UpdateDrawable(float hue)
    {
        float cx = 100f;
        float cy = 100f;

        // 72f aligns precisely with the middle of the drawn Hue Ring (defined below)
        float ringRadius = 72f;

        float angle = hue * 2f * (float)Math.PI;
        float tx = cx + ringRadius * (float)Math.Cos(angle);
        float ty = cy + ringRadius * (float)Math.Sin(angle);

        _drawable.SelectionPoint = new PointF(tx, ty);
        HueWheelView.Invalidate();
    }

    private void OnHexInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating) return;

        try
        {
            if (e.NewTextValue.Length == 7 && e.NewTextValue.StartsWith("#"))
            {
                var color = Color.FromArgb(e.NewTextValue);
                // The SetColor logic will automatically strip out non-pure
                // saturation/lightness from what the user typed.
                if (color != null) SetColor(color);
            }
        }
        catch { /* Ignore invalid hex input gracefully */ }
    }

    private async void OnResetClicked(object sender, EventArgs e)
    {
        _setting.Reset();
        await CloseAsync();
    }

    private async void OnCancelClicked(object sender, EventArgs e) => await CloseAsync();

    private async void OnOkClicked(object sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(HexInput.Text))
            _setting.DisplayValue = HexInput.Text;

        await CloseAsync();
    }

    private string ColorToHex(Color color)
    {
        int r = (int)Math.Round(color.Red * 255);
        int g = (int)Math.Round(color.Green * 255);
        int b = (int)Math.Round(color.Blue * 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}

public class HueWheelDrawable : IDrawable
{
    public PointF SelectionPoint { get; set; } = new PointF(100, 28);

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float cx = dirtyRect.Center.X;
        float cy = dirtyRect.Center.Y;

        float outerRadius = Math.Min(dirtyRect.Width, dirtyRect.Height) / 2f - 16f; // Leaves space for thumb outline
        float innerRadius = outerRadius - 24f; // 24 is the thickness of the hue ring

        // Draw Hue Ring
        for (int i = 0; i < 360; i++)
        {
            float rad1 = (float)(i * Math.PI / 180f);

            // Add +1.5 degrees instead of +1 to force a slight overlap. 
            // This prevents sub-pixel anti-aliasing "cracks" between polygons.
            float rad2 = (float)((i + 1.5f) * Math.PI / 180f);

            var path = new PathF();
            path.MoveTo(cx + innerRadius * (float)Math.Cos(rad1), cy + innerRadius * (float)Math.Sin(rad1));
            path.LineTo(cx + outerRadius * (float)Math.Cos(rad1), cy + outerRadius * (float)Math.Sin(rad1));
            path.LineTo(cx + outerRadius * (float)Math.Cos(rad2), cy + outerRadius * (float)Math.Sin(rad2));
            path.LineTo(cx + innerRadius * (float)Math.Cos(rad2), cy + innerRadius * (float)Math.Sin(rad2));
            path.Close();

            canvas.FillColor = Color.FromHsla(i / 360f, 1f, 0.5f);
            canvas.FillPath(path);
        }

        // Draw selection thumb
        if (SelectionPoint.X >= 0 && SelectionPoint.Y >= 0)
        {
            canvas.StrokeColor = Colors.Black;
            canvas.StrokeSize = 3;
            canvas.DrawCircle(SelectionPoint.X, SelectionPoint.Y, 12);

            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = 2;
            canvas.DrawCircle(SelectionPoint.X, SelectionPoint.Y, 12);
        }
    }
}