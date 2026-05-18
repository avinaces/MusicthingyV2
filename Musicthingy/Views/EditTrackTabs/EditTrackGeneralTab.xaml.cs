using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Musicthingy.Models;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Musicthingy.Views;

public partial class EditTrackGeneralTab : ContentView
{
    public EditTrackGeneralTab()
    {
        InitializeComponent();
    }
}

public partial class EditTrackForm : ObservableObject
{
    [ObservableProperty] public partial string EditTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditStartTime { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditEndTime { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditIconUrl { get; set; } = string.Empty;

    [ObservableProperty] public partial ImageSource? ItemIconSource { get; set; }
    [ObservableProperty] public partial string LastPlayedText { get; set; } = string.Empty;

    public EditTrackForm(PlaylistItem track)
    {
        EditTitle = track.Title;
        EditUrl = track.Url;
        EditStartTime = track.StartTime?.ToString() ?? "";
        EditEndTime = track.EndTime?.ToString() ?? "";
        EditIconUrl = track.IconUrl ?? "";
        ItemIconSource = track.ItemIconSource;

        if (track.LastPlayedAt.HasValue)
        {
            LastPlayedText = track.LastPlayedAt.Value.ToString("M/dd/yyyy, h:mm:ss tt");
        }
    }

    [RelayCommand]
    private async Task PickIconAsync()
    {
        try
        {
            FileResult? result = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Select Track Icon", FileTypes = FilePickerFileType.Images });
            if (result != null)
            {
                using Stream stream = await result.OpenReadAsync();
                byte[]? finalBytes = null;

#if ANDROID
                finalBytes = await CropAndResizeImageAndroidAsync(stream, 256);
#elif WINDOWS
                finalBytes = await CropAndResizeImageWindowsAsync(stream, 256);
#else
                using MemoryStream ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                finalBytes = ms.ToArray();
#endif

                if (finalBytes != null)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        string base64String = Convert.ToBase64String(finalBytes);
                        EditIconUrl = $"data:image/png;base64,{base64String}";
                        ItemIconSource = ImageSource.FromStream(() => new MemoryStream(finalBytes));
                    });
                }
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlertAsync("Error", $"Could not load image: {ex.Message}", "OK");
        }
    }

    [RelayCommand]
    private void ResetIcon()
    {
        EditIconUrl = string.Empty;
        ItemIconSource = IconGenerator.GenerateIcon(string.IsNullOrEmpty(EditTitle) ? "?" : EditTitle, 8, 48);
    }

#if ANDROID
    private async Task<byte[]?> CropAndResizeImageAndroidAsync(Stream stream, int targetSize)
    {
        try
        {
            Android.Graphics.Bitmap? bitmap = await Android.Graphics.BitmapFactory.DecodeStreamAsync(stream);
            if (bitmap == null) return null;

            int minDim = Math.Min(bitmap.Width, bitmap.Height);
            if (minDim <= 0) return null;

            int startX = (bitmap.Width - minDim) / 2;
            int startY = (bitmap.Height - minDim) / 2;

            Android.Graphics.Bitmap? cropped = Android.Graphics.Bitmap.CreateBitmap(bitmap, startX, startY, minDim, minDim);
            Android.Graphics.Bitmap? scaled = Android.Graphics.Bitmap.CreateScaledBitmap(cropped, targetSize, targetSize, true);

            using MemoryStream memoryStream = new MemoryStream();
            await scaled!.CompressAsync(Android.Graphics.Bitmap.CompressFormat.Png!, 100, memoryStream);

            bitmap.Recycle();
            if (cropped != bitmap) cropped.Recycle();
            if (scaled != null && scaled != cropped) scaled.Recycle();

            return memoryStream.ToArray();
        }
        catch { return null; }
    }
#endif

#if WINDOWS
    private async Task<byte[]?> CropAndResizeImageWindowsAsync(Stream stream, int targetSize)
    {
        try
        {
            using MemoryStream memStream = new MemoryStream();
            await stream.CopyToAsync(memStream);
            byte[] imageBytes = memStream.ToArray();

            using Windows.Storage.Streams.InMemoryRandomAccessStream ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (Windows.Storage.Streams.DataWriter writer = new Windows.Storage.Streams.DataWriter(ras))
            {
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            ras.Seek(0);
            
            Windows.Graphics.Imaging.BitmapDecoder decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
            
            uint originalWidth = decoder.PixelWidth;
            uint originalHeight = decoder.PixelHeight;
            uint minDim = Math.Min(originalWidth, originalHeight);
            if (minDim == 0) return null;

            double scale = (double)targetSize / minDim;
            uint scaledW = Math.Max((uint)targetSize, (uint)Math.Round(originalWidth * scale));
            uint scaledH = Math.Max((uint)targetSize, (uint)Math.Round(originalHeight * scale));

            uint cropX = (scaledW - (uint)targetSize) / 2;
            uint cropY = (scaledH - (uint)targetSize) / 2;

            using Windows.Storage.Streams.InMemoryRandomAccessStream outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            Windows.Graphics.Imaging.BitmapEncoder encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, outStream);
            
            Windows.Graphics.Imaging.BitmapTransform transform = new Windows.Graphics.Imaging.BitmapTransform 
            { 
                ScaledWidth = scaledW, 
                ScaledHeight = scaledH,
                Bounds = new Windows.Graphics.Imaging.BitmapBounds { X = cropX, Y = cropY, Width = (uint)targetSize, Height = (uint)targetSize },
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
            };

            Windows.Graphics.Imaging.PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)targetSize, (uint)targetSize, decoder.DpiX, decoder.DpiY, pixels.DetachPixelData());

            await encoder.FlushAsync();
            
            byte[] outBytes = new byte[outStream.Size];
            using Windows.Storage.Streams.DataReader reader = new Windows.Storage.Streams.DataReader(outStream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)outStream.Size);
            reader.ReadBytes(outBytes);
            
            return outBytes;
        }
        catch (Exception ex)
        { 
            System.Diagnostics.Debug.WriteLine($"[Windows Image Resize Error]: {ex.Message}");
            return null; 
        }
    }
#endif
}