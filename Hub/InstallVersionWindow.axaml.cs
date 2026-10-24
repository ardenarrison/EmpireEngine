using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EmpireHub;

// Lists the Empire versions published on GitHub and installs the one you pick: it downloads the release's zip and unpacks
// the editor next to the others, as <hub>/Empire/<version>/. See Releases/ReleaseInstaller.cs for what is checked on the way.
public partial class InstallVersionWindow : Window
{
    private static readonly HttpClient Http = ReleaseCatalog.CreateClient();

    private readonly string _root = ReleaseInstaller.DefaultRoot;
    private CancellationTokenSource? _work;     // the list being fetched, or the install in progress
    private bool _installing;

    // The versions installed while this window was open.
    public List<string> InstalledVersions { get; } = new();

    public InstallVersionWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await LoadAsync();
        Closing += (_, _) => _work?.Cancel();   // closing in the middle of a download stops it
    }

    private async Task LoadAsync()
    {
        _work?.Cancel();
        var cts = _work = new CancellationTokenSource();
        ReleaseList.IsVisible = false;
        MessageText.IsVisible = true;
        MessageText.Text = "Looking for releases on GitHub...";
        StatusText.Text = "";
        try
        {
            var releases = await ReleaseCatalog.FetchAsync(Http, cts.Token);
            if (releases.Count == 0)
            {
                MessageText.Text = "No Empire versions have been published on GitHub yet.";
                return;
            }
            ShowList(releases);
        }
        catch (InstallException ex)
        {
            MessageText.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // the window closed, or Refresh was pressed again
        }
    }

    private List<EmpireRelease> _releases = new();

    private void ShowList(List<EmpireRelease> releases)
    {
        _releases = releases;
        var installed = ReleaseInstaller.InstalledVersions(_root);
        ReleaseList.ItemsSource = releases.Select(r => new ReleaseItem { Release = r, Installed = installed.Contains(r.Version) }).ToList();
        ReleaseList.IsVisible = true;
        MessageText.IsVisible = false;
    }

    private async void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (_installing || (sender as Control)?.DataContext is not ReleaseItem { CanInstall: true } item) return;

        _installing = true;
        ReleaseList.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        CloseButton.Content = "Cancel";
        Progress.IsVisible = true;
        var cts = _work = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(p =>
        {
            Progress.IsIndeterminate = p.Fraction < 0;
            if (p.Fraction >= 0) Progress.Value = p.Fraction;
            StatusText.Text = p.Stage switch
            {
                "Downloading" => $"Downloading Empire {item.Release.Version}... {(p.Fraction >= 0 ? $"{p.Fraction:P0}" : "")}".TrimEnd(),
                "Unpacking" => $"Unpacking Empire {item.Release.Version}...",
                _ => "",
            };
        });

        try
        {
            string folder = await ReleaseInstaller.InstallAsync(Http, item.Release, _root, progress, cts.Token);
            string version = Path.GetFileName(folder);
            InstalledVersions.Add(version);
            StatusText.Text = $"Installed Empire {version}.";
        }
        catch (InstallException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled. Nothing was installed.";
        }
        finally
        {
            _installing = false;
            Progress.IsVisible = false;
            Progress.IsIndeterminate = false;
            ReleaseList.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            CloseButton.Content = "Close";
            if (_releases.Count > 0) ShowList(_releases);   // the version just installed now reads "Installed"
        }
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await LoadAsync();

    // While an install runs this button is Cancel; otherwise it closes the window.
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (_installing) _work?.Cancel();
        else Close();
    }
}
