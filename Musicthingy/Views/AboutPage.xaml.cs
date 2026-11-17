using Musicthingy.Models;

namespace Musicthingy.Views;

public partial class AboutPage : ContentPage
{
    public AboutPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        Shell.SetNavBarIsVisible(this, false);

        VersionLabel.Text = LicenseCatalog.AppVersion;
        SectionsList.ItemsSource = LicenseCatalog.BuildGroups();
    }

    private async void OnBackClicked(object sender, EventArgs e)
    {
        if (Navigation.ModalStack.Count > 0)
            await Navigation.PopModalAsync();
        else
            await Navigation.PopAsync();
    }

    private async void OnEntryTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is not LicenseEntry entry)
            return;

        await Navigation.PushModalAsync(new LicenseDetailPage(entry));
    }

    private async void OnOpenRepoClicked(object sender, TappedEventArgs e)
    {
        await OpenUrlAsync(LicenseCatalog.RepoUrl);
    }

    private async void OnOpenAppLicenseClicked(object sender, TappedEventArgs e)
    {
        await Navigation.PushModalAsync(new LicenseDetailPage(LicenseCatalog.App));
    }

    private static async Task OpenUrlAsync(string url)
    {
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
