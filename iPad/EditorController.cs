using Editor;
using Editor.Ui;
using EmpireHub;
using Foundation;
using UIKit;

namespace EmpireiPad;

// Picks a project from the app's Documents folder (Finder and the Files app can reach it) and runs the editor on it. The
// sample project is copied there on first launch, so it can be saved like any other.
public sealed class EditorController : UIViewController
{
    private readonly EditorView _view = new();
    private readonly UILabel _message = new() { TextColor = UIColor.White, Lines = 0, TextAlignment = UITextAlignment.Center };
    private TouchWindow? _window;

    public override void LoadView() => View = _view;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        _view.BackgroundColor = UIColor.Black;
        _message.Frame = _view.Bounds;
        _message.AutoresizingMask = UIViewAutoresizing.FlexibleDimensions;
        _view.AddSubview(_message);
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        _view.BecomeFirstResponder();
        SetNeedsUpdateOfSupportedInterfaceOrientations();
        View?.Window?.WindowScene?.RequestGeometryUpdate(new UIWindowSceneGeometryPreferencesIOS(UIInterfaceOrientationMask.Landscape), _ => { });
        if (_window is null) PickProject();
    }

    private void PickProject()
    {
        var projects = Projects();
        if (projects.Count == 1)
        {
            Open(projects[0]);
            return;
        }
        var choices = UIAlertController.Create("Open a project", null, UIAlertControllerStyle.Alert);
        foreach (string project in projects)
            choices.AddAction(UIAlertAction.Create(Path.GetFileName(project), UIAlertActionStyle.Default, _ => Open(project)));
        PresentViewController(choices, true, null);
    }

    private static List<string> Projects()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string sample = Path.Combine(documents, "Sample");
        if (!Directory.Exists(sample)) CopyFolder(Path.Combine(NSBundle.MainBundle.ResourcePath!, "Sample"), sample);
        return Directory.GetDirectories(documents).Where(d => File.Exists(Path.Combine(d, "project.empire")))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string folder in Directory.GetDirectories(from)) CopyFolder(folder, Path.Combine(to, Path.GetFileName(folder)));
    }

    private void Open(string project)
    {
        var window = new TouchWindow(_view);
        _window = window;
        _view.Window = window;
        window.UpdateSize();
        Theme.Light = TraitCollection.UserInterfaceStyle != UIUserInterfaceStyle.Dark;

        var thread = new Thread(() =>
        {
            try
            {
                // File > Change Project ends one editor and names the project for the next.
                for (string? next = project; next is not null;)
                {
                    var editor = new EditorApp(EmpireProject.FindStartScene(next) ?? Path.Combine(next, "Scenes/Main.empirescene"), next);
                    editor.Run(window, _ => window);
                    next = editor.NextProject;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                InvokeOnMainThread(() => _message.Text = $"Could not open the project.\n\n{ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");
            }
        }, 16 * 1024 * 1024) { Name = "Editor" };
        thread.Start();
    }

    public void SetActive(bool active) => _window?.SetActive(active);

    // The editor's panels need the width. Asked for in code, because iPadOS no longer goes by Info.plist alone.
    public override UIInterfaceOrientationMask GetSupportedInterfaceOrientations() => UIInterfaceOrientationMask.Landscape;

    public override bool PrefersStatusBarHidden() => true;
    public override bool PrefersHomeIndicatorAutoHidden => true;
}
