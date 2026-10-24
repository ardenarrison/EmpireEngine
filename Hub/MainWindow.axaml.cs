using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace EmpireHub;

public partial class MainWindow : Window
{
    private readonly HubSettings _settings = HubSettings.Load();
    private List<EditorInstall> _installs = new();
    private List<ProjectItem> _projects = new();

    public MainWindow()
    {
        InitializeComponent();
        string version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "?";
        HubVersionText.Text = "Empire Engine Hub " + version;
        AboutVersionText.Text = $"Empire Engine Hub {version}, made with the Empire Engine";
        RefreshInstalls();
        RefreshProjects();
        Opened += (_, _) =>
        {
            SetDockIcon();
            ShowSplash();
        };
    }

    // The hub opens on the Empire Engine and BRUTAL logos (see the Splash overlay in MainWindow.axaml): three seconds, then a
    // half-second fade. Nothing under it can be clicked until it is gone, and there is no way to skip it.
    private const int SplashSeconds = 3;
    private async void ShowSplash()
    {
        await Task.Delay(SplashSeconds * 1000);
        Splash.Opacity = 0;
        await Task.Delay(500);
        Splash.IsVisible = false;
    }

    // The hub is a plain executable, not an .app bundle, so on macOS it sets its own Dock icon once it is running: the
    // crown on white (the window icons on other systems keep the transparent background).
    private static void SetDockIcon()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("EmpireHub.EmpireEngineIcon-mac.png");
        if (stream is null) return;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Engine.Brutal.Gfx.MacDockIcon.Set(copy.ToArray());
    }

    // ---- lists ---------------------------------------------------------------------------

    private void RefreshInstalls()
    {
        _installs = EditorInstall.Discover(_settings);
        InstallList.ItemsSource = _installs;
        NoInstallsText.IsVisible = _installs.Count == 0;
    }

    private void RefreshProjects()
    {
        _projects = _settings.Projects.Select(ProjectItem.From).ToList();
        ApplySearch();
    }

    private void ApplySearch()
    {
        string query = SearchBox.Text?.Trim() ?? "";
        ProjectList.ItemsSource = query.Length == 0
            ? _projects
            : _projects.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                   p.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        NoProjectsText.IsVisible = _projects.Count == 0;
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplySearch();

    private void OnNavChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProjectsPage is null || InstallsPage is null || AboutPage is null) return;   // fires once during InitializeComponent
        ProjectsPage.IsVisible = Nav.SelectedIndex == 0;
        InstallsPage.IsVisible = Nav.SelectedIndex == 1;
        AboutPage.IsVisible = Nav.SelectedIndex == 2;
        if (AboutPage.IsVisible && CreditList.ItemsSource is null) LoadCredits();
    }

    private void Status(string text) => StatusText.Text = text;

    // ---- projects ------------------------------------------------------------------------

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectWindow(_installs, _settings.LastLocation ?? DefaultLocation());
        string? created = await dialog.ShowDialog<string?>(this);
        if (created is null) return;

        _settings.LastLocation = Path.GetDirectoryName(created);
        _settings.Touch(created);
        _settings.Save();
        RefreshProjects();
        Open(created);
    }

    private async void OnAddExisting(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an Empire Engine project folder",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;

        if (!EmpireProject.LooksLikeProject(path))
        {
            Status($"{path} isn't an Empire Engine project: it has no {EmpireProject.FileName} and no scene in Scenes/.");
            return;
        }
        _settings.Touch(path);
        _settings.Save();
        RefreshProjects();
        Status($"Added {Path.GetFileName(path.TrimEnd('/', '\\'))}.");
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Open(item.Path);
    }

    private void OnProjectDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ProjectList.SelectedItem is ProjectItem { Exists: true } item) Open(item.Path);
    }

    private async void OnRevealClick(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { Exists: true } item)
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(item.Path));
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        _settings.Projects.RemoveAll(p => HubSettings.SamePath(p.Path, item.Path));
        _settings.Save();
        RefreshProjects();
        Status($"Removed {item.Name} from the list. Its files are untouched.");
    }

    private static ProjectItem? ItemOf(object? sender) => (sender as Control)?.DataContext as ProjectItem;

    private void Open(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Status($"{folder} no longer exists.");
            return;
        }
        if (EmpireProject.FindStartScene(folder) is not { } scene)
        {
            Status($"No scene found in {Path.Combine(folder, "Scenes")}.");
            return;
        }

        string? wanted = EmpireProject.TryRead(folder)?.EditorVersion;
        if (EditorInstall.ForProject(_installs, wanted) is not { } install)
        {
            Status("No Empire Engine editor is installed. Open the Installs page and press Install version...");
            return;
        }

        try
        {
            var process = install.Launch(scene, folder);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                int code = process.ExitCode;
                if (code != 0)
                    Dispatcher.UIThread.Post(() => Status($"Empire Engine {install.Version} exited with code {code}."));
            };

            _settings.Touch(folder);
            _settings.Save();
            RefreshProjects();

            string name = Path.GetFileName(folder.TrimEnd('/', '\\'));
            Status(wanted is not null && wanted != install.Version
                ? $"Opened {name} with Empire Engine {install.Version} (made with {wanted}, which isn't installed)."
                : $"Opened {name} with Empire Engine {install.Version}.");
        }
        catch (Exception ex)
        {
            Status($"Could not start Empire Engine {install.Version}: {ex.Message}");
        }
    }

    private static string DefaultLocation() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Empire Engine Projects");

    // ---- about ---------------------------------------------------------------------------

    private void LoadCredits()
    {
        try
        {
            var (notice, items) = CreditItem.Load(_installs);
            BrutalNoticeText.Text = notice;
            CreditList.ItemsSource = items;
        }
        catch (Exception ex)
        {
            CreditList.ItemsSource = new List<CreditItem>();
            LicenseText.Text = $"Could not read the license list: {ex.Message}";
        }
    }

    private void OnCreditSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (CreditList.SelectedItem is not CreditItem item) return;
        try
        {
            LicenseText.Text = File.ReadAllText(item.FilePath);
        }
        catch (Exception ex)
        {
            LicenseText.Text = $"Could not read {item.FilePath}: {ex.Message}";
        }
    }

    // ---- installs ------------------------------------------------------------------------

    // Opens the list of Empire Engine versions published on GitHub. Whatever it installs shows up in the list afterwards.
    private async void OnInstallVersion(object? sender, RoutedEventArgs e)
    {
        var window = new InstallVersionWindow();
        await window.ShowDialog(this);
        RefreshInstalls();
        if (window.InstalledVersions.Count > 0)
            Status($"Installed Empire Engine {string.Join(", ", window.InstalledVersions)}.");
    }

    private async void OnLocateInstall(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an Empire Engine editor folder (containing empire.dll)",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;

        if (EditorInstall.FromFolder(path, bundled: false) is not { } install)
        {
            Status($"No empire.dll in {path}. Pick the editor's build folder, such as Editor/bin/Debug/net10.0.");
            return;
        }
        if (_installs.Any(i => HubSettings.SamePath(i.Folder, install.Folder)))
        {
            Status("That editor is already listed.");
            return;
        }
        _settings.LocatedInstalls.Add(install.Folder);
        _settings.Save();
        RefreshInstalls();
        Status($"Added Empire Engine {install.Version}.");
    }

    private void OnRemoveInstall(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not EditorInstall { Bundled: false } install) return;
        _settings.LocatedInstalls.RemoveAll(f => HubSettings.SamePath(f, install.Folder));
        _settings.Save();
        RefreshInstalls();
        Status($"Removed Empire Engine {install.Version} from the list. Its files are untouched.");
    }
}
