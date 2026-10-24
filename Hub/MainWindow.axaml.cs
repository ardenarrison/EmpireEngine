using System.Diagnostics;
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
    private readonly List<(Process Process, string Folder)> _running = new();   // editors this Hub started that are still open
    private HubUpdate? _pendingUpdate;

    public MainWindow()
    {
        InitializeComponent();
        string version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "?";
        HubVersionText.Text = "Empire Engine Hub " + version;
        AboutVersionText.Text = $"Empire Engine Hub {version}, made with the Empire Engine";
        AutoCheckBox.IsChecked = _settings.CheckForUpdates;
        RefreshInstalls();
        RefreshProjects();
        Opened += (_, _) =>
        {
            SetDockIcon();
            ShowSplash();
            HubUpdater.CleanOldUpdates();
            if (HubUpdater.TakeLastError() is { } failure) Status(failure);
            if (_settings.CheckForUpdates) _ = CheckForUpdateQuietly();
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

    private async void Open(string folder)
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
        string name = Path.GetFileName(folder.TrimEnd('/', '\\'));
        EditorInstall? install;
        if (wanted is null || _installs.Any(i => i.Version == wanted))
            install = EditorInstall.ForProject(_installs, wanted);
        else
        {
            // The version the project was made with isn't installed: ask which one to use instead of choosing for the user.
            install = await AskForEditorAsync(folder, name, wanted);
            if (install is null)
            {
                Status($"Didn't open {name}.");
                return;
            }
            wanted = EmpireProject.TryRead(folder)?.EditorVersion;   // changed if they asked to switch the project over
        }
        if (install is null)
        {
            Status("No Empire Engine editor is installed. Open the Installs page and press Install version...");
            return;
        }

        try
        {
            var process = install.Launch(scene, folder);
            process.EnableRaisingEvents = true;
            lock (_running) _running.Add((process, install.Folder));
            process.Exited += (_, _) =>
            {
                lock (_running) _running.RemoveAll(r => r.Process == process);
                int code = process.ExitCode;
                if (code != 0)
                    Dispatcher.UIThread.Post(() => Status($"Empire Engine {install.Version} exited with code {code}."));
            };

            _settings.Touch(folder);
            _settings.Save();
            RefreshProjects();

            Status(wanted is not null && wanted != install.Version
                ? $"Opened {name} with Empire Engine {install.Version} (made with {wanted}, which isn't installed)."
                : $"Opened {name} with Empire Engine {install.Version}.");
        }
        catch (Exception ex)
        {
            Status($"Could not start Empire Engine {install.Version}: {ex.Message}");
        }
    }

    // Asks which installed editor to open a project with when its own version isn't installed. They can also choose to install
    // that version (then they are asked again, with it in the list) or to switch the project to the one they pick.
    private async Task<EditorInstall?> AskForEditorAsync(string folder, string name, string wanted)
    {
        for (;;)
        {
            var result = await EditorVersionWindow.ForMissing(name, wanted, _installs).ShowDialog<EditorVersionResult?>(this);
            if (result is null) return null;

            if (result.InstallWanted)
            {
                await ShowInstallVersionWindow();
                if (_installs.FirstOrDefault(i => i.Version == wanted) is { } installed) return installed;
                continue;
            }

            if (_installs.FirstOrDefault(i => i.Version == result.Version) is not { } chosen) continue;
            if (result.Remember)
            {
                try
                {
                    EmpireProject.SetEditorVersion(folder, chosen.Version);
                    RefreshProjects();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Status($"Couldn't change {name}'s editor version: {ex.Message}");
                }
            }
            return chosen;
        }
    }

    private async void OnChangeVersionClick(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { Exists: true } item) return;
        var result = await EditorVersionWindow.ForChange(item.Name, item.EditorVersion, _installs).ShowDialog<EditorVersionResult?>(this);
        if (result is null) return;
        if (result.Version == item.EditorVersion)
        {
            Status($"{item.Name} still uses {(item.EditorVersion is null ? "any version" : "Empire Engine " + item.EditorVersion)}.");
            return;
        }

        try
        {
            EmpireProject.SetEditorVersion(item.Path, result.Version);
            RefreshProjects();
            Status(result.Version is null
                ? $"{item.Name} now opens with the newest installed version."
                : $"{item.Name} now opens with Empire Engine {result.Version}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status($"Couldn't change {item.Name}'s editor version: {ex.Message}");
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
    private async void OnInstallVersion(object? sender, RoutedEventArgs e) => await ShowInstallVersionWindow();

    private async Task ShowInstallVersionWindow()
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

    // Remove on a located editor only forgets it. Uninstall on one in the Hub's folder deletes it, after asking.
    private async void OnRemoveInstall(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not EditorInstall install) return;

        if (!install.Bundled)
        {
            _settings.LocatedInstalls.RemoveAll(f => HubSettings.SamePath(f, install.Folder));
            _settings.Save();
            RefreshInstalls();
            Status($"Removed Empire Engine {install.Version} from the list. Its files are untouched.");
            return;
        }

        bool open;
        lock (_running) open = _running.Any(r => !r.Process.HasExited && HubSettings.SamePath(r.Folder, install.Folder));
        if (open)
        {
            Status($"Empire Engine {install.Version} is open. Close it first, then uninstall it.");
            return;
        }

        var ask = new ConfirmWindow($"Uninstall Empire Engine {install.Version}?",
            $"This deletes {install.Folder} and everything in it. Your projects are not touched; a project made with this version " +
            "will open with the newest installed version until you install this one again.", "Uninstall");
        if (!await ask.ShowDialog<bool>(this)) return;

        try
        {
            await Task.Run(() => ReleaseInstaller.Uninstall(ReleaseInstaller.DefaultRoot, install.Folder));
            Status($"Uninstalled Empire Engine {install.Version}.");
        }
        catch (InstallException ex)
        {
            Status(ex.Message);
        }
        RefreshInstalls();
    }

    // ---- updating the hub ------------------------------------------------------------------

    // Looks once for a newer Hub and shows the banner if there is one. Failures are ignored: no connection is not worth a message here.
    private async Task CheckForUpdateQuietly()
    {
        try
        {
            using var http = ReleaseCatalog.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (await HubUpdater.CheckAsync(http, cts.Token) is not { } update) return;
            _pendingUpdate = update;
            UpdateBannerText.Text = $"Empire Engine Hub {update.Version} is available.";
            UpdateBanner.IsVisible = true;
        }
        catch (Exception ex) when (ex is InstallException or OperationCanceledException) { }
    }

    private async void OnUpdateBannerClick(object? sender, RoutedEventArgs e) => await new UpdateWindow(_pendingUpdate).ShowDialog(this);

    private async void OnCheckForUpdates(object? sender, RoutedEventArgs e) => await new UpdateWindow(null).ShowDialog(this);

    private void OnAutoCheckChanged(object? sender, RoutedEventArgs e)
    {
        _settings.CheckForUpdates = AutoCheckBox.IsChecked == true;
        _settings.Save();
    }
}
