using Editor.Ui;
using Engine.Core.Assets;

namespace Editor;

// A window that lists files under the content root, for picking one. Models by default (.glb, .gltf, .fbx, .obj);
// pass another filter to pick something else, such as a sky image.
public sealed class ModelBrowser
{
    private string _contentRoot = "";
    private string _current = "";
    private bool _open;
    private Func<string, bool> _filter = ModelLoader.IsModel;
    private string _title = "Select Model";

    public bool IsOpen => _open;
    public void Close() => _open = false;

    public static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".hdr";

    // startFolder is where the list opens when there is no current file, relative to the content root.
    public void Open(string contentRoot, string? currentRelativeModel, Func<string, bool>? filter = null, string title = "Select Model",
        string startFolder = "Models")
    {
        _filter = filter ?? ModelLoader.IsModel;
        _title = title;
        _contentRoot = Path.GetFullPath(contentRoot);
        string start = Path.Combine(_contentRoot, startFolder);
        if (!string.IsNullOrEmpty(currentRelativeModel))
        {
            string dir = Path.GetDirectoryName(Path.Combine(_contentRoot, currentRelativeModel))!;
            if (Directory.Exists(dir)) start = dir;
        }
        _current = Directory.Exists(start) ? start : _contentRoot;
        _open = true;
    }

    // Draws the window. Returns a content-relative path (forward slashes) when a file is picked, else null.
    public string? Draw(Gui g)
    {
        if (!_open) return null;

        string? picked = null;
        var rect = new Box((g.Size.X - 420f) / 2f, 70f, 420f, Math.Min(460f, g.Size.Y - 100f));
        if (!g.BeginWindow("Select Model", rect, _title, WindowFlags.Closable | WindowFlags.NoScroll))
            _open = false;

        g.TextDisabled(RelativeCurrent());
        if (g.Button("Up", enabled: !IsRoot()) && !IsRoot())
            _current = Path.GetDirectoryName(_current)!;
        g.SameLine();
        if (g.Button("Cancel"))
            _open = false;

        g.BeginChild("files", 0f, 0f);
        int id = 0;
        bool navigated = false;
        foreach (var dir in Directory.GetDirectories(_current).OrderBy(d => d))
        {
            if (g.Selectable("[DIR] " + SafeLabel.Of(Path.GetFileName(dir), 60) + "##d" + id++, false))
            {
                _current = dir;
                navigated = true;
                break;
            }
        }

        if (!navigated)
        {
            foreach (var file in Directory.GetFiles(_current).OrderBy(f => f))
            {
                if (!_filter(file))
                    continue;
                if (g.Selectable(SafeLabel.Of(Path.GetFileName(file), 60) + "##f" + id++, false))
                {
                    picked = Path.GetRelativePath(_contentRoot, file).Replace('\\', '/');
                    _open = false;
                    break;
                }
            }
        }
        g.EndChild();
        g.EndWindow();
        return picked;
    }

    private bool IsRoot() =>
        string.Equals(Path.GetFullPath(_current).TrimEnd('/', '\\'), _contentRoot.TrimEnd('/', '\\'),
            StringComparison.OrdinalIgnoreCase);

    private string RelativeCurrent()
    {
        var rel = Path.GetRelativePath(_contentRoot, _current).Replace('\\', '/');
        return rel == "." ? "/" : rel;
    }
}
