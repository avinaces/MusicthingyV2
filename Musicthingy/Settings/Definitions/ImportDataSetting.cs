using System.IO.Compression;
using CommunityToolkit.Maui.Storage;
using Musicthingy.Models;
using System.Text.Json;

namespace Musicthingy.Settings.Definitions;

public class ImportDataSetting : ActionSetting
{
    public override string Key => "ImportAppData";
    public override string Title => "Import Data";
    public override string Icon => "upload_icon.png";
    public override SettingCategory Category => SettingCategory.Storage;

    private bool _isImporting;
    protected override string GetBaseDescription() => _isImporting ? "Importing..." : "Import library data from a backup zip file";

    public override async void ExecuteAction()
    {
        if (_isImporting) return;
        _isImporting = true;
        OnPropertyChanged(nameof(Description));

        try
        {
            await ImportDataAsync();
        }
        finally
        {
            _isImporting = false;
            MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(Description)));
        }
    }

    private async Task ImportDataAsync()
    {
        try
        {
            // Pick a zip file
            var result = await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                return await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = "Select a Musicthingy backup zip",
                    FileTypes = new FilePickerFileType(
                        new Dictionary<DevicePlatform, IEnumerable<string>>
                        {
                            { DevicePlatform.Android, new[] { "application/zip", "application/octet-stream" } },
                            { DevicePlatform.WinUI, new[] { ".zip" } },
                        })
                });
            });

            if (result == null) return;

            string zipPath = result.FullPath;
            if (!File.Exists(zipPath)) return;

            // Extract on background thread
            await Task.Run(() =>
            {
                string appDataPath = FileSystem.AppDataDirectory;
                string tempExtractPath = Path.Combine(FileSystem.CacheDirectory, "import_temp");

                // Clean up any previous extraction
                if (Directory.Exists(tempExtractPath))
                    Directory.Delete(tempExtractPath, true);
                Directory.CreateDirectory(tempExtractPath);

                // Extract the zip
                ZipFile.ExtractToDirectory(zipPath, tempExtractPath, overwriteFiles: true);

                // Check for library.json
                string extractedLibrary = Path.Combine(tempExtractPath, "library.json");
                if (File.Exists(extractedLibrary))
                {
                    // Validate it's a real library file by trying to parse it
                    string json = File.ReadAllText(extractedLibrary);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var playlists = JsonSerializer.Deserialize<List<Playlist>>(json, options);

                    if (playlists != null)
                    {
                        // Backup current library
                        string currentLibrary = Path.Combine(appDataPath, "library.json");
                        if (File.Exists(currentLibrary))
                        {
                            string backupPath = currentLibrary + ".backup";
                            File.Copy(currentLibrary, backupPath, overwrite: true);
                        }

                        // Copy the imported library
                        File.Copy(extractedLibrary, currentLibrary, overwrite: true);
                    }
                }

                // Clean up temp
                Directory.Delete(tempExtractPath, true);
            });

            // Show success + prompt restart
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                if (page != null)
                    await page.DisplayAlertAsync(
                        "Import Complete",
                        "Library data imported successfully.\nPlease restart the app to load the imported data.",
                        "OK");
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Import] Failed: {ex.Message}");
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                if (page != null)
                    await page.DisplayAlertAsync("Import Failed", ex.Message, "OK");
            });
        }
    }
}
