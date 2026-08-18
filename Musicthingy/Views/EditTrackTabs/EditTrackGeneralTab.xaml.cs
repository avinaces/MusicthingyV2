using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Maui.Controls;
using Musicthingy.Helpers;
using Musicthingy.Models;
using Musicthingy.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
    private readonly PlaylistItem _originalTrack;

    [ObservableProperty] public partial string EditTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditStartTime { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditEndTime { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditIconUrl { get; set; } = string.Empty;

    [ObservableProperty] public partial ImageSource? ItemIconSource { get; set; }
    [ObservableProperty] public partial string LastPlayedText { get; set; } = string.Empty;
    [ObservableProperty] public partial int Views { get; set; } = 0;

    // --- Validation Properties ---
    [ObservableProperty] public partial string TitleError { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasTitleError { get; set; }

    [ObservableProperty] public partial string UrlError { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasUrlError { get; set; }

    [ObservableProperty] public partial bool IsCheckingUrl { get; set; }

    // --- Chapters Properties ---
    [ObservableProperty] public partial bool HasChapters { get; set; }
    [ObservableProperty] public partial int ChaptersCount { get; set; }
    private IReadOnlyList<VideoChapter>? _availableChapters;

    // --- Playlist Import Properties ---
    [ObservableProperty] public partial bool HasPlaylistVideos { get; set; }
    [ObservableProperty] public partial int PlaylistVideosCount { get; set; }
    private IReadOnlyList<YouTubePlaylistVideo>? _availablePlaylistVideos;

    private CancellationTokenSource? _urlCheckCts;

    public EditTrackForm(PlaylistItem track)
    {
        _originalTrack = track;

        EditTitle = track.Title;
        EditUrl = track.Url;
        EditStartTime = track.StartTime?.ToString() ?? "";
        EditEndTime = track.EndTime?.ToString() ?? "";
        EditIconUrl = track.IconUrl ?? "";
        ItemIconSource = track.ItemIconSource;
        Views = track.Views;

        if (track.LastPlayedAt.HasValue)
        {
            LastPlayedText = track.LastPlayedAt.Value.ToString("M/dd/yyyy, h:mm:ss tt");
        }

        ValidateTitleFast(EditTitle);
        ValidateUrlFast(EditUrl);
    }

    partial void OnEditTitleChanged(string value) => ValidateTitleFast(value);

    private void ValidateTitleFast(string title)
    {
        HasTitleError = string.IsNullOrWhiteSpace(title);
        TitleError = HasTitleError ? "Name is required." : string.Empty;
    }

    partial void OnEditUrlChanged(string value)
    {
        ValidateUrlFast(value);

        // Reset both chapter and playlist states on URL change
        HasChapters = false;
        _availableChapters = null;
        HasPlaylistVideos = false;
        _availablePlaylistVideos = null;

        if (!HasUrlError && !string.IsNullOrWhiteSpace(value))
        {
            _urlCheckCts?.Cancel();
            _urlCheckCts = new CancellationTokenSource();
            _ = DebouncedUrlCheckAsync(value, _urlCheckCts.Token);
        }
    }

    private void ValidateUrlFast(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            HasUrlError = true;
            UrlError = "URL is required.";
            return;
        }

        bool isHttp = Uri.TryCreate(url, UriKind.Absolute, out Uri? uriResult)
                      && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        bool isLocalFile = File.Exists(url);

        if (!isHttp && !isLocalFile)
        {
            HasUrlError = true;
            UrlError = "Must be a valid HTTP(S) URL or local file path.";
        }
        else
        {
            HasUrlError = false;
            UrlError = string.Empty;
        }
    }

    private async Task DebouncedUrlCheckAsync(string url, CancellationToken token)
    {
        try
        {
            IsCheckingUrl = true;
            await Task.Delay(800, token);

            (bool isSuccess, string errorMessage, string? fetchedTitle, string? fetchedImageUrl) =
                await NetworkHelper.CheckMediaUrlAsync(url, token);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!isSuccess)
                {
                    HasUrlError = true;
                    UrlError = errorMessage;
                }
                else
                {
                    HasUrlError = false;
                    UrlError = string.Empty;

                    if (!string.IsNullOrWhiteSpace(fetchedTitle) && string.IsNullOrWhiteSpace(EditTitle))
                    {
                        EditTitle = fetchedTitle;
                    }
                }
            });

            if (isSuccess && MediaUrlHelper.IsYouTubeUrl(url))
            {
                YouTubeResolverService resolver = new YouTubeResolverService();

                // 1. Check if URL is a YouTube Playlist
                if (MediaUrlHelper.IsYouTubePlaylistUrl(url))
                {
                    IReadOnlyList<YouTubePlaylistVideo> playlistVideos = await resolver.GetPlaylistVideosAsync(url, token);
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (playlistVideos != null && playlistVideos.Count > 0)
                        {
                            _availablePlaylistVideos = playlistVideos;
                            PlaylistVideosCount = playlistVideos.Count;
                            HasPlaylistVideos = true;
                        }
                    });
                }

                // 2. Check if video has Chapters
                IReadOnlyList<VideoChapter> chapters = await resolver.GetVideoChaptersAsync(url);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (chapters != null && chapters.Count > 0)
                    {
                        _availableChapters = chapters;
                        ChaptersCount = chapters.Count;
                        HasChapters = true;
                    }
                });
            }

            if (isSuccess && !string.IsNullOrWhiteSpace(fetchedImageUrl) && string.IsNullOrWhiteSpace(EditIconUrl))
            {
                await ProcessAndSetFetchedIconAsync(fetchedImageUrl, token);
            }
        }
        catch (TaskCanceledException) { }
        finally
        {
            MainThread.BeginInvokeOnMainThread(() => IsCheckingUrl = false);
        }
    }

    [RelayCommand]
    private async Task SplitChaptersAsync()
    {
        if (_availableChapters == null || _availableChapters.Count == 0) return;

        // Fix: Use Application.Current.Windows instead of Shell.Current
        Page? activePage = Application.Current?.Windows.FirstOrDefault()?.Page;
        bool confirm = true;

        if (activePage != null)
        {
            confirm = await activePage.DisplayAlertAsync(
                "Split Video",
                $"This will discard your current edits and generate {_availableChapters.Count} separate tracks using the video's chapters. Continue?",
                "Split", "Cancel");
        }

        if (!confirm) return;

        // Broadcast to MainViewModel so it can do the actual database additions
        WeakReferenceMessenger.Default.Send(new SplitTrackIntoChaptersMessage(
            _originalTrack,
            _availableChapters,
            EditUrl,
            EditIconUrl));

        // Returns false to the caller so they drop the currently edited single track
        await SheetManager.CloseCurrentAsync(false);
    }

    private async Task ProcessAndSetFetchedIconAsync(string imageUrl, CancellationToken token)
    {
        try
        {
            using HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using HttpResponseMessage response = await client.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, token);

            if (response.IsSuccessStatusCode)
            {
                using Stream stream = await response.Content.ReadAsStreamAsync(token);
                byte[]? finalBytes = null;

#if ANDROID
                finalBytes = await CropAndResizeImageAndroidAsync(stream, 256);
#elif WINDOWS
                finalBytes = await CropAndResizeImageWindowsAsync(stream, 256);
#else
                using MemoryStream ms = new MemoryStream();
                await stream.CopyToAsync(ms, token);
                finalBytes = ms.ToArray();
#endif

                if (finalBytes != null)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (string.IsNullOrWhiteSpace(EditIconUrl))
                        {
                            string base64String = Convert.ToBase64String(finalBytes);
                            EditIconUrl = $"data:image/png;base64,{base64String}";
                            ItemIconSource = ImageSource.FromStream(() => new MemoryStream(finalBytes));
                        }
                    });
                }
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to download/crop fetched icon: {ex.Message}");
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
    private async Task ImportPlaylistVideosAsync()
    {
        if (_availablePlaylistVideos == null || _availablePlaylistVideos.Count == 0) return;

        Page? activePage = Application.Current?.Windows.FirstOrDefault()?.Page;
        bool confirm = true;

        if (activePage != null)
        {
            confirm = await activePage.DisplayAlertAsync(
                "Import Playlist",
                $"This will import {_availablePlaylistVideos.Count} tracks from this playlist. Continue?",
                "Import", "Cancel");
        }

        if (!confirm) return;

        // Broadcast to MainViewModel to insert the tracks
        WeakReferenceMessenger.Default.Send(new ImportPlaylistTracksMessage(
            _originalTrack,
            _availablePlaylistVideos));

        await SheetManager.CloseCurrentAsync(false);
    }

// Add this message record at the bottom of the file

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
public record SplitTrackIntoChaptersMessage(PlaylistItem OriginalTrack, IReadOnlyList<VideoChapter> Chapters, string VideoUrl, string IconUrl);
public record ImportPlaylistTracksMessage(PlaylistItem OriginalTrack, IReadOnlyList<YouTubePlaylistVideo> Videos);