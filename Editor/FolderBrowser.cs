using Editor.Ui;

namespace Editor;

// Folder browser drawn inside the current window. Click a folder to go into it, Up to go back, and Select This Folder
// to choose the current one.
public sealed class FolderBrowser
{
    private string _current = "";
    private bool _open;

    public bool IsOpen => _open;

    public void Open(string startFolder)
    {
        _current = Directory.Exists(startFolder)
            ? Path.GetFullPath(startFolder)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _open = true;
    }

    public void Close() => _open = false;

    // Returns the chosen folder when the user presses Select. listHeight is the height of the scrolling folder list.
    public string? Draw(Gui g, float listHeight)
    {
        if (!_open) return null;

        string? picked = null;
        g.TextWrapped(_current, Gui.DimColor);

        if (g.Button("Up") && Path.GetDirectoryName(_current) is { } parent)
            _current = parent;
        g.SameLine();
        if (g.Button("Select This Folder", highlighted: true))
        {
            picked = _current;
            _open = false;
        }

        g.BeginChild("folders", 0f, listHeight);
        int id = 0;
        foreach (var dir in SubFolders(_current))
        {
            if (g.Selectable("[DIR] " + SafeLabel.Of(Path.GetFileName(dir), 60) + "##" + id++, false))
            {
                _current = dir;
                break;
            }
        }
        g.EndChild();

        return picked;
    }

    private static IEnumerable<string> SubFolders(string path)
    {
        try
        {
            return Directory.GetDirectories(path).OrderBy(d => d).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
