
using System.IO.Compression;
using CommunityToolkit.Maui.Storage;

namespace Musicthingy.Settings.Definitions
{
    public class ExportDataSetting : ActionSetting
    {
        // Updated keys to reflect the actual action, rather than Theme Colors
        public override string Key => "ExportAppData";
        public override string Title => "Export Data";
        public override SettingCategory Category => SettingCategory.Storage;

        protected override string GetBaseDescription() => "Export all library data and settings to a zip file";

        public override async void ExecuteAction()
        {
            await ExportDataAsync();
        }

        private async Task ExportDataAsync()
        {
            string tempZipPath = string.Empty;

            try
            {
                // 1. This grabs your library.json and all settings files
                string appDataPath = FileSystem.AppDataDirectory;

                // 2. We MUST save the zip to the Cache directory. 
                // If we save it to AppDataDirectory, the zip will try to zip itself!
                string zipFileName = $"Musicthingy_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
                tempZipPath = Path.Combine(FileSystem.CacheDirectory, zipFileName);

                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }

                // 3. Compress the entire data directory (creates the .zip)
                ZipFile.CreateFromDirectory(appDataPath, tempZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

                // 4. Open the file to hand off to the Android System
                using var stream = new FileStream(tempZipPath, FileMode.Open, FileAccess.Read);

                // 5. Trigger the Android "Save As..." dialog (allows saving to Downloads folder)
                var result = await FileSaver.Default.SaveAsync(zipFileName, stream, CancellationToken.None);

                if (result.IsSuccessful)
                {
                    System.Diagnostics.Debug.WriteLine($"Successfully exported data to: {result.FilePath}");

                    
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Export failed: {ex.Message}");
                // Optional: Show failure alert
            }
            finally
            {
                // 6. Cleanup: Delete the temporary zip file from the cache so we don't waste phone storage
                if (!string.IsNullOrEmpty(tempZipPath) && File.Exists(tempZipPath))
                {
                    try
                    {
                        File.Delete(tempZipPath);
                    }
                    catch { /* Ignore cleanup errors */ }
                }
            }
        }
    }
}