using Musicthingy.Models;

namespace Musicthingy.Views;

/// <summary>
/// Details for one About-page row: what it is, where it comes from and the full license text
/// (bundled in Resources/Raw/licenses, so this works without a network connection).
/// </summary>
public partial class LicenseDetailPage : ContentPage
{
    private readonly LicenseEntry _entry;
    private bool _textLoaded;

    public LicenseDetailPage(LicenseEntry entry)
    {
        InitializeComponent();

        NavigationPage.SetHasNavigationBar(this, false);
        Shell.SetNavBarIsVisible(this, false);

        _entry = entry;
        BindingContext = entry;

        BadgeLabel.Text = entry.BadgeText;
        NameLabel.Text = entry.Name;
        MetaLabel.Text = string.IsNullOrEmpty(entry.VersionLabel)
            ? entry.License.Name
            : $"{entry.VersionLabel}  ·  {entry.License.Name}";

        SummaryLabel.Text = entry.Summary;

        CopyrightLabel.Text = entry.Copyright ?? string.Empty;
        CopyrightLabel.IsVisible = !string.IsNullOrWhiteSpace(entry.Copyright);

        LicenseChipLabel.Text = entry.License.Id;

        PackagesSection.IsVisible = entry.IsGroup;
        if (entry.IsGroup)
        {
            PackageCountChip.IsVisible = true;
            PackageCountLabel.Text = entry.Packages.Count == 1
                ? "1 PACKAGE"
                : $"{entry.Packages.Count} PACKAGES";

            // The legend only makes sense for the Xamarin/AndroidX wrappers.
            PackageLegendLabel.IsVisible = entry.Packages.Any(p => p.HasNote);
        }

        ProjectButton.IsVisible = !string.IsNullOrWhiteSpace(entry.ProjectUrl);
        LicenseButton.IsVisible = !string.IsNullOrWhiteSpace(entry.License.Url);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_textLoaded)
            return;

        _textLoaded = true;
        await LoadLicenseTextAsync();
    }

    private async Task LoadLicenseTextAsync()
    {
        string? asset = _entry.License.TextAsset;
        if (string.IsNullOrEmpty(asset))
        {
            TextHeaderLabel.Text = "LICENSE";
            LicenseTextLabel.Text = "This license has no redistributable text. Use \"License online\" to read it.";
            return;
        }

        try
        {
            using Stream stream = await FileSystem.OpenAppPackageFileAsync(asset);
            using StreamReader reader = new(stream);
            LicenseTextLabel.Text = (await reader.ReadToEndAsync()).Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[About] Could not read {asset}: {ex.Message}");
            LicenseTextLabel.Text = "The bundled license text could not be read. Use \"License online\" instead.";
        }
    }

    private async void OnBackClicked(object sender, EventArgs e)
    {
        if (Navigation.ModalStack.Count > 0)
            await Navigation.PopModalAsync();
        else
            await Navigation.PopAsync();
    }

    private async void OnProjectClicked(object sender, EventArgs e)
    {
        await OpenUrlAsync(_entry.ProjectUrl);
    }

    private async void OnLicenseClicked(object sender, EventArgs e)
    {
        await OpenUrlAsync(_entry.License.Url);
    }

    private static async Task OpenUrlAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            await Launcher.Default.OpenAsync(url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[About] Could not open {url}: {ex.Message}");
        }
    }
}
