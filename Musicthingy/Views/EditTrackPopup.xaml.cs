using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Models;
using System.Collections.ObjectModel;

namespace Musicthingy.Views;

public partial class EditTrackPopup : Popup<bool>
{
    private readonly PlaylistItem _originalTrack;
    private readonly EditTrackForm _form;
    private readonly List<string> _allTags; // Keep reference to tags

    public EditTrackPopup(PlaylistItem track, List<string> allTags)
    {
        InitializeComponent();

        _originalTrack = track;
        _allTags = allTags;

        _form = new EditTrackForm
        {
            EditTitle = track.Title,
            EditUrl = track.Url,
            EditStartTime = track.StartTime?.ToString() ?? "",
            EditEndTime = track.EndTime?.ToString() ?? "",
            EditLyrics = track.Lyrics ?? "",
            EditIconUrl = track.IconUrl ?? "",

            ItemIconSource = track.ItemIconSource,
            LastPlayedText = track.LastPlayedAt?.ToString("M/dd/yyyy, h:mm:ss tt") ?? "",
            Tags = new ObservableCollection<string>(track.Tags)
        };

        BindingContext = _form;

#if ANDROID
        var displayInfo = DeviceDisplay.Current.MainDisplayInfo;
        double logicalWidth = displayInfo.Width / displayInfo.Density;
        double logicalHeight = displayInfo.Height / displayInfo.Density;

        RootBorder.WidthRequest = logicalWidth * 0.949;
        RootBorder.MaximumHeightRequest = logicalHeight * 0.899;
#endif
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await CloseAsync(false);
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        _originalTrack.Title = _form.EditTitle ?? string.Empty;
        _originalTrack.Url = _form.EditUrl ?? string.Empty;
        _originalTrack.Lyrics = _form.EditLyrics;
        _originalTrack.IconUrl = _form.EditIconUrl;

        if (int.TryParse(_form.EditStartTime, out int start)) _originalTrack.StartTime = start;
        else _originalTrack.StartTime = null;

        if (int.TryParse(_form.EditEndTime, out int end)) _originalTrack.EndTime = end;
        else _originalTrack.EndTime = null;

        _originalTrack.Tags.Clear();
        foreach (var tag in _form.Tags) _originalTrack.Tags.Add(tag);

        await CloseAsync(true);
    }

    private async void OnEditLyricsClicked(object? sender, EventArgs e)
    {
        var lyricsPopup = new EditLyricsPopup(_form.EditLyrics, _form.EditTitle, _originalTrack.Artist ?? "");
        var activePage = Shell.Current ?? Application.Current?.Windows.FirstOrDefault()?.Page;

        if (activePage != null)
        {
            var popupResult = await activePage.ShowPopupAsync<bool>(lyricsPopup);
            if (popupResult != null && popupResult.Result)
            {
                _form.EditLyrics = lyricsPopup.Form.LyricsText;
            }
        }
    }

    private async void OnEditTagsClicked(object? sender, EventArgs e)
    {
        var tagsPopup = new EditTagsPopup(_form.Tags.ToList(), _allTags);
        var activePage = Shell.Current ?? Application.Current?.Windows.FirstOrDefault()?.Page;

        if (activePage != null)
        {
            var rawResult = await activePage.ShowPopupAsync<object>(tagsPopup);

            // 1. Check if the result is directly the list
            List<string>? selectedTags = rawResult as List<string>;

            // 2. Fallback: If it's wrapped (e.g., has a .Result property), safely extract it
            if (selectedTags == null && rawResult != null)
            {
                var resultProp = rawResult.GetType().GetProperty("Result");
                if (resultProp != null)
                {
                    selectedTags = resultProp.GetValue(rawResult) as List<string>;
                }
            }

            // 3. If we successfully got our tags list back, apply them
            if (selectedTags != null)
            {
                _form.Tags.Clear();
                foreach (var tag in selectedTags)
                {
                    _form.Tags.Add(tag);

                    // Add to the master list of all known tags if it's new
                    if (!_allTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        _allTags.Add(tag);
                    }
                }
            }
        }
    }
}

public partial class EditTrackForm : ObservableObject
{
    [ObservableProperty] public partial string EditTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditStartTime { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditEndTime { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditLyrics { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditIconUrl { get; set; } = string.Empty;

    [ObservableProperty] public partial ImageSource? ItemIconSource { get; set; }
    [ObservableProperty] public partial string LastPlayedText { get; set; } = string.Empty;

    [ObservableProperty] public partial ObservableCollection<string> Tags { get; set; } = new();

    [RelayCommand]
    private async Task PickIconAsync()
    {
        try
        {
            // FIX: Using FilePicker instead of the obsolete MediaPicker.PickPhotoAsync
            // This prevents System.ArgumentException and properly targets file browsing.
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select Track Icon",
                FileTypes = FilePickerFileType.Images
            });

            if (result != null)
            {
                using var stream = await result.OpenReadAsync();
                byte[]? finalBytes = null;

#if ANDROID
                finalBytes = await CropAndResizeImageAndroidAsync(stream, 256);
#elif WINDOWS
                finalBytes = await CropAndResizeImageWindowsAsync(stream, 256);
#else
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                finalBytes = ms.ToArray();
#endif

                if (finalBytes != null)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        var b64 = Convert.ToBase64String(finalBytes);
                        EditIconUrl = $"data:image/png;base64,{b64}";
                        ItemIconSource = ImageSource.FromStream(() => new MemoryStream(finalBytes));
                    });
                }
            }
        }
        catch (Exception ex)
        {
            var activePage = Shell.Current ?? Application.Current?.Windows.FirstOrDefault()?.Page;
            if (activePage != null)
                await activePage.DisplayAlertAsync("Error", $"Could not load image: {ex.Message}", "OK");
        }
    }

    [RelayCommand]
    private void ResetIcon()
    {
        EditIconUrl = string.Empty;

        // Let the system generate a fresh placeholder immediately using the current title
        ItemIconSource = IconGenerator.GenerateIcon(string.IsNullOrEmpty(EditTitle) ? "?" : EditTitle, 8, 48);
    }

#if ANDROID
    private async Task<byte[]?> CropAndResizeImageAndroidAsync(Stream stream, int targetSize)
    {
        try
        {
            var bitmap = await Android.Graphics.BitmapFactory.DecodeStreamAsync(stream);
            if (bitmap == null) return null;

            int minDim = Math.Min(bitmap.Width, bitmap.Height);
            if (minDim <= 0) return null; // Safe check

            int x = (bitmap.Width - minDim) / 2;
            int y = (bitmap.Height - minDim) / 2;

            var cropped = Android.Graphics.Bitmap.CreateBitmap(bitmap, x, y, minDim, minDim);
            var scaled = Android.Graphics.Bitmap.CreateScaledBitmap(cropped, targetSize, targetSize, true);

            using var ms = new MemoryStream();

            // FIX: Added null-forgiving operator (!) to suppress the parameter format warning.
            await scaled!.CompressAsync(Android.Graphics.Bitmap.CompressFormat.Png!, 100, ms);

            bitmap.Recycle();
            if (cropped != bitmap) cropped.Recycle();
            if (scaled != null && scaled != cropped) scaled.Recycle();

            return ms.ToArray();
        }
        catch { return null; }
    }
#endif

#if WINDOWS
    private async Task<byte[]?> CropAndResizeImageWindowsAsync(Stream stream, int targetSize)
    {
        try
        {
            // Read the MAUI stream fully into a managed byte array
            using var memStream = new MemoryStream();
            await stream.CopyToAsync(memStream);
            byte[] imageBytes = memStream.ToArray();

            // Write the bytes directly to a purely native WinRT stream
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(ras))
            {
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            ras.Seek(0);
            
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
            
            uint w = decoder.PixelWidth;
            uint h = decoder.PixelHeight;
            uint minDim = Math.Min(w, h);
            if (minDim == 0) return null;

            // FIX: WinRT applies Scaling BEFORE Cropping!
            // 1. Scale the image down so the smallest side matches targetSize
            double scale = (double)targetSize / minDim;
            uint scaledW = Math.Max((uint)targetSize, (uint)Math.Round(w * scale));
            uint scaledH = Math.Max((uint)targetSize, (uint)Math.Round(h * scale));

            // 2. Calculate the crop coordinates relative to the newly SCALED image
            uint cropX = (scaledW - (uint)targetSize) / 2;
            uint cropY = (scaledH - (uint)targetSize) / 2;

            using var outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, outStream);
            
            var transform = new Windows.Graphics.Imaging.BitmapTransform 
            { 
                ScaledWidth = scaledW, 
                ScaledHeight = scaledH,
                Bounds = new Windows.Graphics.Imaging.BitmapBounds 
                { 
                    X = cropX, 
                    Y = cropY, 
                    Width = (uint)targetSize, 
                    Height = (uint)targetSize 
                },
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
            };

            var pixels = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);

            // Because we cropped a targetSize x targetSize area, 
            // the pixel buffer strictly matches the target dimensions.
            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)targetSize,
                (uint)targetSize,
                decoder.DpiX,
                decoder.DpiY,
                pixels.DetachPixelData());

            await encoder.FlushAsync();
            
            // Read the native outStream back to managed bytes
            var outBytes = new byte[outStream.Size];
            using var reader = new Windows.Storage.Streams.DataReader(outStream.GetInputStreamAt(0));
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