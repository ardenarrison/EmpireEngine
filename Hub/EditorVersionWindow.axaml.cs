using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EmpireHub;

// What the user chose. Version is null for "any version" (the newest installed). InstallWanted means they asked to install
// the version the project wants instead of choosing an installed one.
public sealed record EditorVersionResult(string? Version, bool Remember, bool InstallWanted);

// Asks which editor version a project should use. Two uses: a project whose version isn't installed (pick another, or install
// it), and changing the version a project uses. Closes with an EditorVersionResult, or null when cancelled.
public partial class EditorVersionWindow : Window
{
    private readonly List<string?> _versions = new();   // the version behind each row of the list; null is "any version"

    public EditorVersionWindow() => InitializeComponent();

    // The project wants a version that isn't installed.
    public static EditorVersionWindow ForMissing(string project, string wanted, IReadOnlyList<EditorInstall> installs)
    {
        var window = new EditorVersionWindow();
        window.Title = $"Empire Engine {wanted} isn't installed";
        window.TitleText.Text = window.Title;
        window.MessageText.Text = installs.Count == 0
            ? $"{project} was made with Empire Engine {wanted}, and no editor is installed yet."
            : $"{project} was made with Empire Engine {wanted}, which isn't installed. Choose a version to open it with, or install {wanted}.";
        string? suggested = EditorVersions.Suggest(wanted, installs.Select(i => i.Version));
        window.Fill(installs, suggested, includeAny: false, notInstalled: null);
        window.RememberBox.IsVisible = installs.Count > 0;
        window.NoteText.Text = "A project saved by a newer version may not open in an older one. Back it up first if you're unsure.";
        window.PrimaryButton.Content = "Open";
        window.PrimaryButton.IsEnabled = installs.Count > 0;
        window.InstallButton.Content = $"Install {wanted}...";
        window.InstallButton.IsVisible = true;
        window._wanted = wanted;
        return window;
    }

    // Change which version a project uses. current is what it uses now (null for any version).
    public static EditorVersionWindow ForChange(string project, string? current, IReadOnlyList<EditorInstall> installs)
    {
        var window = new EditorVersionWindow();
        window.Title = "Change editor version";
        window.TitleText.Text = $"Change the editor version for {project}";
        window.MessageText.Text = current is null
            ? "It opens with the newest installed version now. Choose the version it should always open with."
            : $"It opens with Empire Engine {current} now. Choose the version it should open with instead.";
        window.Fill(installs, current, includeAny: true, notInstalled: current);
        window.NoteText.Text = "This only changes which editor opens the project; its files aren't converted. A project saved by a newer " +
                               "version may not open in an older one, so back it up first if you're going back.";
        window.PrimaryButton.Content = "Save";
        return window;
    }

    private string? _wanted;

    private void Fill(IReadOnlyList<EditorInstall> installs, string? select, bool includeAny, string? notInstalled)
    {
        var labels = new List<string>();
        if (includeAny)
        {
            labels.Add("Any version (the newest installed)");
            _versions.Add(null);
        }
        if (notInstalled is not null && !installs.Any(i => i.Version == notInstalled))
        {
            labels.Add($"Empire Engine {notInstalled} (not installed)");
            _versions.Add(notInstalled);
        }
        foreach (var install in installs)
        {
            labels.Add($"Empire Engine {install.Version}");
            _versions.Add(install.Version);
        }
        VersionBox.ItemsSource = labels;
        VersionBox.IsEnabled = labels.Count > 0;
        int index = select is null ? 0 : _versions.FindIndex(v => v == select);
        VersionBox.SelectedIndex = labels.Count == 0 ? -1 : Math.Max(0, index);
    }

    private void OnPrimary(object? sender, RoutedEventArgs e)
    {
        int i = VersionBox.SelectedIndex;
        if (i < 0 || i >= _versions.Count) return;
        Close(new EditorVersionResult(_versions[i], RememberBox.IsChecked == true, InstallWanted: false));
    }

    private void OnInstall(object? sender, RoutedEventArgs e) => Close(new EditorVersionResult(_wanted, false, InstallWanted: true));
    private void OnCancel(object? sender, RoutedEventArgs e) => Close((EditorVersionResult?)null);
}
