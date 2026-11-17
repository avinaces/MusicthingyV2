using System.Text;
using Musicthingy.Models;

namespace Musicthingy.Views;

/// <summary>
/// Details for one About-page row: what it is, where it comes from and the full license text
/// (bundled in Resources/Raw/licenses, so this works without a network connection).
/// A bundled group can carry several licenses, so one text block is built per license present.
/// </summary>
public partial class LicenseDetailPage : ContentPage
{
    private readonly LicenseEntry _entry;
    private bool _contentBuilt;

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

        if (_contentBuilt)
            return;

        _contentBuilt = true;
        await BuildLicenseTextsAsync();
    }

    /// <summary>One header + text card per license the entry ships under.</summary>
    private async Task BuildLicenseTextsAsync()
    {
        List<LicenseTextBlock> blocks = [];

        foreach ((LicenseKind kind, int packageCount) in LicensesForEntry())
        {
            LicenseInfo info = LicenseCatalog.GetKind(kind);
            string header = info.Name.ToUpperInvariant();
            if (packageCount > 0)
                header += packageCount == 1 ? " · 1 PACKAGE" : $" · {packageCount} PACKAGES";

            blocks.Add(new LicenseTextBlock(header, await ReadLicenseTextAsync(info)));
        }

        BindableLayout.SetItemsSource(LicenseTextsSection, blocks);
    }

    private IEnumerable<(LicenseKind Kind, int PackageCount)> LicensesForEntry()
    {
        if (!_entry.IsGroup)
            return [(_entry.Kind, 0)];

        return _entry.Packages
            .GroupBy(package => package.Kind)
            .OrderByDescending(group => group.Count())
            .Select(group => (group.Key, group.Count()))
            .ToList();
    }

    private static async Task<string> ReadLicenseTextAsync(LicenseInfo info)
    {
        if (string.IsNullOrEmpty(info.TextAsset))
            return "This license has no redistributable text. Use \"License online\" to read it.";

        try
        {
            using Stream stream = await FileSystem.OpenAppPackageFileAsync(info.TextAsset);
            using StreamReader reader = new(stream);
            return Reflow(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[About] Could not read {info.TextAsset}: {ex.Message}");
            return "The bundled license text could not be read. Use \"License online\" instead.";
        }
    }

    /// <summary>
    /// License files are hard-wrapped at ~70 characters, which fights the Label's own wrapping
    /// and leaves single orphan words. Join each block of consecutive lines into one paragraph,
    /// keep the blank lines that separate them, and pull out the two things these texts glue to
    /// the next line: separator rules and short ALL-CAPS headings (PREAMBLE, DEFINITIONS, ...).
    /// </summary>
    private static string Reflow(string raw)
    {
        string[] lines = raw.Replace("\r\n", "\n").Split('\n');
        StringBuilder result = new();
        StringBuilder paragraph = new();

        void FlushParagraph()
        {
            if (paragraph.Length == 0)
                return;

            result.Append(paragraph).Append('\n').Append('\n');
            paragraph.Clear();
        }

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            // "-----" and friends frame the title, so they get a paragraph of their own.
            if (trimmed.Length >= 3 && trimmed.All(c => !char.IsLetterOrDigit(c)))
            {
                FlushParagraph();
                result.Append(trimmed).Append('\n').Append('\n');
                continue;
            }

            if (paragraph.Length > 0)
                paragraph.Append(' ');

            paragraph.Append(trimmed);

            if (IsShortAllCaps(trimmed))
                FlushParagraph();
        }

        FlushParagraph();
        return result.ToString().Trim();
    }

    /// <summary>
    /// True for the standalone headings the license texts put on their own line without a blank
    /// line after them. Long all-caps runs (the warranty disclaimers) are paragraphs, not headings.
    /// </summary>
    private static bool IsShortAllCaps(string line)
    {
        if (line.Length > 40)
            return false;

        bool hasLetter = false;
        foreach (char c in line)
        {
            if (!char.IsLetter(c))
                continue;

            if (char.IsLower(c))
                return false;

            hasLetter = true;
        }

        return hasLetter;
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
