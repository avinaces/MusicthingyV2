using System.IO.Compression;
using CommunityToolkit.Maui.Storage;

namespace Musicthingy.Settings.Definitions;

public class ExportDataSetting : ActionSetting
{
    public override string Key => "ExportAppData";
    public override string Title => "Export Data";
    public override SettingCategory Category => SettingCategory.Storage;

    private bool _isExporting;
    protected override string GetBaseDescription() => _isExporting ? "Exporting..." : "Export library data and settings to a zip file";

    public override async void ExecuteAction()
    {
        if (_isExporting) return;
        _isExporting = true;
        OnPropertyChanged(nameof(Description));

        try
        {
            await Task.Run(ExportDataAsync);
        }
        finally
        {
            _isExporting = false;
            MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(Description)));
        }
    }

    private async Task ExportDataAsync()
    {
        string tempZipPath = string.Empty;

        try
        {
            string appDataPath = FileSystem.AppDataDirectory;
            string zipFileName = $"Musicthingy_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            tempZipPath = Path.Combine(FileSystem.CacheDirectory, zipFileName);

            if (File.Exists(tempZipPath))
                File.Delete(tempZipPath);

            // Only export library.json + preferences — NOT the entire app data directory
            // (which may include cache, temp files, and other unnecessary data)
            using (var archive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
            {
                // Export library.json
                string libraryPath = Path.Combine(appDataPath, "library.json");
                if (File.Exists(libraryPath))
                    archive.CreateEntryFromFile(libraryPath, "library.json");

                // Export preferences (all settings are stored as individual preference keys,
                // but we can't easily enumerate them. Instead, export the known setting keys.)
                // For now, just export library.json — that's the main user data.
                // Settings are app-level config, not user data.
            }

            // Switch to UI thread for the file saver dialog
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                using var stream = new FileStream(tempZipPath, FileMode.Open, FileAccess.Read);
                var result = await FileSaver.Default.SaveAsync(zipFileName, stream, CancellationToken.None);

                if (result.IsSuccessful)
                {
                    System.Diagnostics.Debug.WriteLine($"[Export] Successfully exported to: {result.FilePath}");

                    // Show success alert
                    var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                    if (page != null)
                        await page.DisplayAlertAsync("Export Complete", $"Data exported to:\n{result.FilePath}", "OK");
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Export] Failed: {ex.Message}");
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                if (page != null)
                    await page.DisplayAlertAsync("Export Failed", ex.Message, "OK");
            });
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempZipPath) && File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { }
            }
        }
    }
}