using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;

namespace EmpireHub;

// Looks for a newer Hub on GitHub and, on request, downloads it and restarts into it. See Releases/HubUpdater.cs for how
// the files are swapped and what is checked on the way.
public partial class UpdateWindow : Window
{
    private static readonly HttpClient Http = ReleaseCatalog.CreateClient();

    private readonly HubUpdate? _known;         // an update the Hub has already found, so the window opens straight on it
    private HubUpdate? _update;
    private CancellationTokenSource? _work;
    private bool _busy;
    private bool _opensPage;                    // this copy can't replace itself, so the button opens the release page instead

    public UpdateWindow() => InitializeComponent();

    public UpdateWindow(HubUpdate? known) : this()
    {
        _known = known;
        Opened += async (_, _) => { if (_known is not null) Show(_known); else await CheckAsync(); };
        Closing += (_, _) => _work?.Cancel();
    }

    private async Task CheckAsync()
    {
        var cts = _work = new CancellationTokenSource();
        try
        {
            var found = await HubUpdater.CheckAsync(Http, cts.Token);
            if (found is null)
            {
                MessageText.Text = $"You have the newest version: Empire Engine Hub {HubUpdater.CurrentVersion}.";
                return;
            }
            Show(found);
        }
        catch (InstallException ex)
        {
            MessageText.Text = ex.Message;
        }
        catch (OperationCanceledException) { }
    }

    private void Show(HubUpdate update)
    {
        _update = update;
        MessageText.Text = $"Empire Engine Hub {update.Version} is available. You have {HubUpdater.CurrentVersion}.";
        NotesText.Text = update.Notes.Length > 0 ? update.Notes : "No release notes.";
        NotesBox.IsVisible = true;
        _opensPage = !HubUpdater.CanReplace(AppContext.BaseDirectory, Environment.ProcessPath, out string reason);
        ActionButton.Content = _opensPage ? "Open the release page" : "Update and restart";
        ActionButton.IsVisible = true;
        StatusText.Text = _opensPage ? reason : "Your projects and installed editors are kept. The Hub closes and opens again by itself.";
    }

    private async void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _update is not { } update) return;

        if (_opensPage)
        {
            if (Uri.TryCreate(update.PageUrl, UriKind.Absolute, out var page) && page.Scheme == Uri.UriSchemeHttps)
                await Launcher.LaunchUriAsync(page);
            return;
        }

        _busy = true;
        ActionButton.IsEnabled = false;
        CloseButton.Content = "Cancel";
        Progress.IsVisible = true;
        var cts = _work = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(p =>
        {
            Progress.IsIndeterminate = p.Fraction < 0;
            if (p.Fraction >= 0) Progress.Value = p.Fraction;
            StatusText.Text = p.Stage switch
            {
                "Downloading" => $"Downloading Empire Engine Hub {update.Version}... {(p.Fraction >= 0 ? $"{p.Fraction:P0}" : "")}".TrimEnd(),
                "Unpacking" => $"Unpacking Empire Engine Hub {update.Version}...",
                _ => "",
            };
        });

        try
        {
            string folder = await HubUpdater.DownloadAsync(Http, update, progress, cts.Token);
            StatusText.Text = "Restarting...";
            HubUpdater.LaunchApply(folder, HubUpdater.CurrentRoot);
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (InstallException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled. Nothing was changed.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"The update was downloaded but couldn't be started: {ex.Message}";
        }
        finally
        {
            _busy = false;
            Progress.IsVisible = false;
            Progress.IsIndeterminate = false;
            ActionButton.IsEnabled = true;
            CloseButton.Content = "Close";
        }
    }

    // While the update downloads this button is Cancel; otherwise it closes the window.
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) _work?.Cancel();
        else Close();
    }
}
