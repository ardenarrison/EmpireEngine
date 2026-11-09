using Brutal.Numerics;
using Editor.Ui;
using Engine.Core.Assets;
using Engine.Core.Audio;
using Engine.Core.Serialization;

namespace Editor;

// Docked along the bottom of the editor, full window width. Browses the project folder and its subfolders.
// Model files (.glb, .gltf, .fbx, .obj) can be dragged from here and dropped on the scene to place them. Sound files
// (.mp3, .wav) can be dragged onto an Audio Source in the Inspector, or onto the scene to make an object that plays them.
// Prefabs (.pre) can be dragged onto the scene to place a copy.
// Models that contain animations show a small arrow: click it to list the animations right after the model, and drag
// one onto the Clip field of an Animator in the Inspector.
// Click a scene to see and change its settings (such as the shadow resolution) in the Inspector.
// Right-click a file to copy, cut, rename or delete it. Right-click anywhere else to create a scene, script, folder or text file, or to
// paste. A folder offers both, for the inside of that folder.
public sealed class AssetPanel
{
    public const float Height = 220f;   // logical pixels

    private const float TileSize = 88f;
    private const float NameHeight = 20f;
    private const float Spacing = 8f;
    private const float DragThreshold = 6f;

    private static float4 TileColor => Theme.Pick(0x2F3136, 0xE6E6E8);
    private static float4 TileHover => Theme.Pick(0x3C3F45, 0xD8D8DC);
    private static float4 FolderColor => Theme.Pick(0xDCB67A, 0x9A6B00);
    private static float4 ModelColor => Theme.Pick(0x4EC9B0, 0x0B8A6F);
    private static float4 SoundColor => Theme.Pick(0xC586C0, 0x9B3FA0);
    private static float4 SceneColor => Theme.Pick(0x569CD6, 0x1F66B8);
    private static float4 PrefabColor => Theme.Pick(0xE5A15C, 0xB05F00);
    private static float4 AnimationColor => Theme.Pick(0xD7BA7D, 0x8A6D1B);
    private static float4 AnimationTile => Theme.Pick(0x383630, 0xECE6D6);

    private readonly string _root;
    private string _current;
    private List<FileSystemInfo> _entries = new();
    private bool _dirty = true;

    private string? _pressedFile;       // model or sound under the mouse when the button went down
    private float2 _pressCursor;
    private string? _dragPath;          // model or sound being dragged right now
    private (string ModelPath, float2 Cursor)? _drop;

    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);       // models whose animations are listed
    private readonly Dictionary<string, IReadOnlyList<string>> _animationNames = new(StringComparer.OrdinalIgnoreCase);
    private (string Model, string Clip)? _pressedClip;   // animation under the mouse when the button went down
    private (string Model, string Clip)? _dragClip;      // animation being dragged right now

    private string? _clickedScene;      // a scene tile clicked since the editor last asked

    // The scene whose settings the Inspector shows (relative to the project folder), so its tile can be marked.
    public string? InspectedScene { get; set; }

    private enum NameDialog { None, Scene, Script, Folder, TextFile, Rename }

    private NameDialog _dialog;          // the dialog asking for a name, if one is open
    private string _dialogName = "";
    private string _dialogFolder = "";   // where it creates, or the file it renames
    private string _message = "";        // what the last action did, shown in the panel until the next one

    private string? _deleting;           // the file or folder the Delete dialog is asking about
    private List<string> _deleteUsers = new();   // what still points at it
    private string? _menuEntry;          // the file or folder whose menu is open
    private bool _rightClickedTile;
    private (string Path, bool Cut)? _clipboard;

    public AssetPanel(string projectRoot)
    {
        _root = Path.GetFullPath(projectRoot);
        _current = _root;
    }

    // Reads the folder again, after something else has changed what is in it.
    public void Reload() => _dirty = true;

    // The file being dragged right now (relative to the project folder), or null.
    public string? DraggedPath => _dragPath;

    // Returns a scene file clicked in the panel (relative to the project folder), once.
    public string? TakeClickedScene()
    {
        var scene = _clickedScene;
        _clickedScene = null;
        return scene;
    }

    // The animation being dragged right now: the model file it is in (relative to the project folder) and its name.
    public (string Model, string Clip)? DraggedClip => _dragClip;

    // Ends the drag without a drop, for a target that has already taken the file.
    public void CancelDrag()
    {
        _dragPath = null;
        _pressedFile = null;
        _dragClip = null;
        _pressedClip = null;
    }

    // Returns a model or sound released over the scene during the last drag, once. The path is relative to the project folder.
    public (string ModelPath, float2 Cursor)? TakeDrop()
    {
        var drop = _drop;
        _drop = null;
        return drop;
    }

    // False while files shouldn't move, such as during play.
    public bool CanChange { get; set; } = true;

    // The scene that is open, as a full path and as it stands now, with any unsaved changes.
    public string OpenScene { get; set; } = "";
    public Func<string>? OpenSceneJson { get; set; }

    // Saves scripts open in the code editor, so renaming one doesn't lose what was typed.
    public Action? SaveScripts { get; set; }

    // Raised after a file or folder has moved, or a script changed name. Files on disk that point at it have already followed.
    public event Action<ProjectFiles.Moved>? Moved;

    // Raised after a file or folder has been deleted.
    public event Action? Deleted;

    public void Draw(Gui g)
    {
        g.BeginWindow("Assets", new Box(0f, g.Size.Y - Height, g.Size.X, Height), "Assets");
        DrawNavigation(g);
        g.Separator();
        _rightClickedTile = false;
        DrawTiles(g);

        if (g.BeginPopup("asset-tile"))
        {
            DrawTileMenu(g);
            g.EndPopup();
        }

        // A right click on a tile gets that tile's menu above, not this one for the panel.
        if (!_rightClickedTile && g.BeginContextMenu("assets", width: 180f))
        {
            DrawFolderCommands(g, _current);
            g.EndPopup();
        }
        g.EndWindow();

        DrawNameDialog(g);
        DrawDeleteDialog(g);
        UpdateDrag(g);
    }

    private void DrawTileMenu(Gui g)
    {
        string entry = _menuEntry!;
        bool canMove = CanChange && ProjectFiles.WhyFixed(_root, entry) is null;
        if (g.MenuItem("Copy", enabled: ProjectFiles.WhyNotCopied(_root, entry) is null))
            Hold(entry, cut: false);
        if (g.MenuItem("Cut", enabled: canMove))
            Hold(entry, cut: true);
        if (g.MenuItem("Rename", enabled: canMove))
        {
            bool folder = Directory.Exists(entry);
            Open(g, NameDialog.Rename, entry, folder ? Path.GetFileName(entry) : Path.GetFileNameWithoutExtension(entry));
        }
        if (g.MenuItem("Delete", enabled: CanChange && ProjectFiles.DeleteProblem(_root, entry, OpenScene) is null))
            AskToDelete(entry);
        // A folder also offers what the panel does, for the inside of that folder.
        if (Directory.Exists(entry))
        {
            g.Separator();
            DrawFolderCommands(g, entry);
        }
    }

    private void DrawFolderCommands(Gui g, string folder)
    {
        if (g.MenuItem("Create Scene")) Open(g, NameDialog.Scene, folder);
        if (g.MenuItem("Create Script")) Open(g, NameDialog.Script, Path.Combine(_root, ProjectFiles.ScriptsFolder));
        if (g.MenuItem("Create Folder")) Open(g, NameDialog.Folder, folder);
        if (g.MenuItem("Create Text File")) Open(g, NameDialog.TextFile, folder);
        g.Separator();
        if (g.MenuItem("Paste", enabled: CanChange && _clipboard is not null))
            Paste(folder);
    }

    private void Open(Gui g, NameDialog dialog, string folder, string name = "")
    {
        _dialog = dialog;
        _dialogFolder = folder;
        _dialogName = name;
        g.FocusNextInput();
    }

    private void AskToDelete(string entry)
    {
        _deleting = entry;
        _deleteUsers = ProjectFiles.UsedBy(_root, entry);
        // The open scene as it stands, which can differ from its file either way.
        string scene = RelativePath(OpenScene);
        _deleteUsers.Remove(scene);
        if (OpenSceneJson?.Invoke() is { } json && ProjectFiles.Uses(json, _root, entry))
            _deleteUsers.Insert(0, scene);
    }

    // Asks before deleting, because it can't be undone, and says what still points at the file.
    private void DrawDeleteDialog(Gui g)
    {
        if (_deleting is not { } entry) return;

        var rect = new Box((g.Size.X - 460f) / 2f, g.Size.Y * 0.28f, 460f, 190f);
        if (!g.BeginWindow("DeleteDialog", rect, "Delete", WindowFlags.Modal | WindowFlags.Closable | WindowFlags.NoScroll))
            _deleting = null;

        bool folder = Directory.Exists(entry);
        g.TextWrapped($"Delete {SafeLabel.Of(RelativePath(entry), 60)}{(folder ? " and everything in it" : "")}? This can't be undone.");
        if (_deleteUsers.Count > 0)
        {
            string names = string.Join(", ", _deleteUsers.Take(3).Select(Path.GetFileName));
            string more = _deleteUsers.Count > 3 ? $" and {_deleteUsers.Count - 3} more" : "";
            g.Spacing();
            g.TextWrapped($"Still used by {SafeLabel.Of(names, 80)}{more}. What uses it will go without it.", Gui.ErrorColor);
        }

        g.Spacing();
        if (g.Button("Delete"))
        {
            _deleting = null;
            Change($"Could not delete {Path.GetFileName(entry)}", () =>
            {
                ProjectFiles.Delete(entry);
                if (_clipboard is { } held && (string.Equals(held.Path, entry, StringComparison.OrdinalIgnoreCase) || held.Path.StartsWith(entry + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    _clipboard = null;
                Done($"Deleted {RelativePath(entry)}");
                Deleted?.Invoke();
            });
        }
        g.SameLine();
        if (g.Button("Cancel", highlighted: true))
            _deleting = null;
        g.EndWindow();
    }

    private void Hold(string entry, bool cut)
    {
        _clipboard = (entry, cut);
        _message = $"{(cut ? "Cut" : "Copied")} {Path.GetFileName(entry)}. Right-click where it should go and choose Paste.";
    }

    private void Paste(string folder)
    {
        var (source, cut) = _clipboard!.Value;
        if (ProjectFiles.PasteProblem(_root, source, folder, cut) is { } problem)
        {
            _message = problem;
            return;
        }
        Change($"Could not paste {Path.GetFileName(source)}", () =>
        {
            var (moved, path) = ProjectFiles.Paste(_root, source, folder, cut);
            if (cut) _clipboard = null;
            Done($"{(cut ? "Moved" : "Copied")} to {RelativePath(path)}", moved);
        });
    }

    // Does something to the project's files, and says so in the panel if it fails.
    private void Change(string failure, Action action)
    {
        try
        {
            SaveScripts?.Invoke();
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _message = $"{failure}: {ex.Message}";
            Console.WriteLine(_message);
        }
        _dirty = true;
    }

    private void Done(string message, ProjectFiles.Moved? moved = null)
    {
        _message = message;
        Console.WriteLine(message + ".");
        if (moved is not null) Moved?.Invoke(moved);
    }

    // Closes the dialog asking for a name if one is open. Returns true when it was, so Escape closes only that.
    public bool CloseDialog()
    {
        if (_deleting is not null)
        {
            _deleting = null;
            return true;
        }
        if (_dialog == NameDialog.None) return false;
        _dialog = NameDialog.None;
        return true;
    }

    private string? DialogProblem() => _dialog switch
    {
        NameDialog.Scene => SceneFiles.Validate(_dialogFolder, _dialogName),
        NameDialog.Script => ProjectFiles.ScriptProblem(_root, _dialogName),
        NameDialog.Folder => ProjectFiles.FolderProblem(_dialogFolder, _dialogName),
        NameDialog.TextFile => ProjectFiles.TextFileProblem(_dialogFolder, _dialogName),
        _ => ProjectFiles.RenameProblem(_root, _dialogFolder, _dialogName),
    };

    // How a script loads a scene: scenes in the Scenes folder by name, anywhere else by path from the project folder.
    private string SceneHint(string rel)
    {
        string name = SceneFiles.CleanName(_dialogName);
        string shown = name.Length == 0 ? "Name" : SafeLabel.Of(name, 40);
        bool inScenes = rel == "Scenes" || rel.StartsWith("Scenes/", StringComparison.Ordinal);
        string call = inScenes
            ? $"SceneManager.LoadScene(\"{(rel.Length > 7 ? rel[7..] + "/" : "")}{shown}\")"
            : $"SceneManager.LoadScene(\"{(rel == "." ? "" : rel + "/")}{shown}.empirescene\")";
        return "Load it from a script with " + call;
    }

    // Asks for a name, then creates or renames.
    private void DrawNameDialog(Gui g)
    {
        if (_dialog == NameDialog.None) return;

        string title = _dialog switch
        {
            NameDialog.Scene => "Create Scene",
            NameDialog.Script => "Create Script",
            NameDialog.Folder => "Create Folder",
            NameDialog.TextFile => "Create Text File",
            _ => "Rename",
        };
        var rect = new Box((g.Size.X - 460f) / 2f, g.Size.Y * 0.28f, 460f, 230f);
        if (!g.BeginWindow("NameDialog", rect, title, WindowFlags.Modal | WindowFlags.Closable | WindowFlags.NoScroll))
            _dialog = NameDialog.None;

        string rel = Path.GetRelativePath(_root, _dialogFolder).Replace('\\', '/');
        string where = rel == "." ? "Project folder" : rel;
        g.TextDisabled((_dialog == NameDialog.Rename ? "Renaming: " : "In: ") + SafeLabel.Of(where, 60));
        g.InputText("Name", ref _dialogName, 64);

        string? problem = _dialogName.Length == 0 ? null : DialogProblem();
        if (problem is not null)
            g.TextWrapped(problem, Gui.ErrorColor);
        else if (_dialog == NameDialog.Scene)
            g.TextWrapped(SceneHint(rel), Gui.DimColor);
        else if (_dialog == NameDialog.Script)
            g.TextWrapped("Scripts go in the Scripts folder, the only one that is compiled.", Gui.DimColor);
        else if (_dialog == NameDialog.Rename)
            g.TextWrapped("Scenes and prefabs that use it follow. Scripts that name it in their code don't.", Gui.DimColor);

        g.Spacing();
        bool valid = _dialogName.Length > 0 && problem is null;
        if ((g.Button(_dialog == NameDialog.Rename ? "Rename" : "Create", enabled: valid, highlighted: valid) || (g.EnterPressed && valid)) && valid)
        {
            var dialog = _dialog;
            _dialog = NameDialog.None;
            Change("Could not do that", () => Commit(dialog));
        }
        g.SameLine();
        if (g.Button("Cancel"))
            _dialog = NameDialog.None;
        g.EndWindow();
    }

    private void Commit(NameDialog dialog)
    {
        switch (dialog)
        {
            case NameDialog.Scene:
                Done($"Created {RelativePath(SceneFiles.Create(_dialogFolder, _dialogName))}");
                break;
            case NameDialog.Script:
                string name = ScriptFiles.ToClassName(_dialogName);
                ScriptFiles.Create(_dialogFolder, name);
                Done($"Created {ProjectFiles.ScriptsFolder}/{name}.cs");
                break;
            case NameDialog.Folder:
                Done($"Created {RelativePath(ProjectFiles.CreateFolder(_dialogFolder, _dialogName))}");
                break;
            case NameDialog.TextFile:
                Done($"Created {RelativePath(ProjectFiles.CreateTextFile(_dialogFolder, _dialogName))}");
                break;
            default:
                var moved = ProjectFiles.Rename(_root, _dialogFolder, _dialogName);
                if (moved.OldPath == moved.NewPath) break;   // it already had that name
                if (_clipboard is { } held && string.Equals(held.Path, _dialogFolder, StringComparison.OrdinalIgnoreCase)) _clipboard = null;
                Done($"Renamed to {moved.NewPath}", moved);
                break;
        }
    }

    private void UpdateDrag(Gui g)
    {
        if (_dragPath is null && _pressedFile is { } pending && g.MouseDown &&
            Distance(g.Mouse, _pressCursor) > DragThreshold)
        {
            _dragPath = pending;
            _pressedFile = null;
        }

        if (_dragClip is null && _pressedClip is { } pendingClip && g.MouseDown &&
            Distance(g.Mouse, _pressCursor) > DragThreshold)
        {
            _dragClip = pendingClip;
            _pressedClip = null;
        }

        if (!g.MouseDown)
        {
            if (_dragPath is { } released)
                _drop = (released, g.Mouse);
            _dragPath = null;
            _pressedFile = null;
            _dragClip = null;   // an animation only goes into an Animator's field, which has taken it by now
            _pressedClip = null;
        }

        if (_dragPath is { } dragging)
            g.DragLabel(SafeLabel.Of(Path.GetFileName(dragging), 40));
        else if (_dragClip is { } clip)
            g.DragLabel(SafeLabel.Of(clip.Clip, 40));
    }

    private void DrawNavigation(Gui g)
    {
        if (g.Button("Project##root"))
            Navigate(_root);

        string rel = Path.GetRelativePath(_root, _current);
        if (rel != ".")
        {
            string acc = _root;
            var parts = rel.Split(Path.DirectorySeparatorChar);
            for (int i = 0; i < parts.Length; i++)
            {
                acc = Path.Combine(acc, parts[i]);
                g.SameLine();
                if (g.Button($"> {parts[i]}##crumb{i}"))
                {
                    Navigate(acc);
                    break;
                }
            }
        }

        g.SameLine();
        if (g.Button("Refresh##refresh"))
            _dirty = true;
        if (_message.Length > 0)
        {
            g.SameLine();
            g.TextDisabled(_message);
        }
    }

    private void DrawTiles(Gui g)
    {
        if (_dirty) Refresh();

        int perRow = Math.Max(1, (int)((g.AvailWidth + Spacing) / (TileSize + Spacing)));
        string? navigateTo = null;
        int placed = 0;   // tiles drawn so far, including animation tiles, which share the grid
        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            bool isDir = entry is DirectoryInfo;
            bool isModel = !isDir && ModelLoader.IsModel(entry.Name);
            bool isSound = !isDir && AudioClip.IsAudio(entry.Name);
            bool isSprite = !isDir && Engine.Core.Scene.SpriteRenderer.IsSpriteImage(entry.Name);
            bool isScene = !isDir && entry.Name.EndsWith(SceneFiles.Extension, StringComparison.OrdinalIgnoreCase);
            bool isPrefab = !isDir && SceneSerializer.IsPrefab(entry.Name);
            var animations = isModel ? AnimationsOf(entry.FullName) : Array.Empty<string>();

            if (placed++ % perRow != 0) g.SameLine();
            var box = g.Reserve(TileSize, TileSize + NameHeight);
            var tile = new Box(box.X, box.Y, TileSize, TileSize);
            bool clicked = g.Behavior($"tile:{entry.FullName}", tile, out bool hovered, out _);

            // The arrow comes after the tile, so a press on it belongs to the arrow and not to a drag of the model.
            bool overArrow = false;
            if (animations.Count > 0)
            {
                var arrow = new Box(tile.X + tile.W - 22f, tile.Y + 2f, 20f, 20f);
                bool open = _expanded.Contains(entry.FullName);
                if (g.Behavior($"arrow:{entry.FullName}", arrow, out overArrow, out _))
                {
                    if (open) _expanded.Remove(entry.FullName);
                    else _expanded.Add(entry.FullName);
                }
            }

            if (g.RightPressed && hovered)
            {
                _rightClickedTile = true;
                _menuEntry = entry.FullName;
                _message = ProjectFiles.WhyFixed(_root, entry.FullName) ?? "";
                g.OpenPopup("asset-tile", g.Mouse, 180f);
            }

            if (isDir && clicked)
                navigateTo = entry.FullName;
            if (isScene && clicked)
                _clickedScene = RelativePath(entry.FullName);
            if ((isModel || isSound || isSprite || isPrefab) && g.MousePressed && hovered && !overArrow && _dragPath is null)
            {
                _pressedFile = RelativePath(entry.FullName);
                _pressCursor = g.Mouse;
            }

            var c = g.C;
            c.Rect(tile.X, tile.Y, tile.W, tile.H, hovered ? TileHover : TileColor);
            if (animations.Count > 0)
            {
                // The arrow goes over the tile's background.
                var arrow = new Box(tile.X + tile.W - 22f, tile.Y + 2f, 20f, 20f);
                c.Rect(arrow.X, arrow.Y, arrow.W, arrow.H, overArrow ? TileColor : TileHover);
                string glyph = _expanded.Contains(entry.FullName) ? "v" : ">";
                c.Text(glyph, arrow.X + (arrow.W - c.MeasureText(glyph)) / 2f, g.TextTop(arrow.Y, arrow.H), AnimationColor);
            }
            if (isScene && InspectedScene is { } inspected && string.Equals(inspected, RelativePath(entry.FullName), StringComparison.OrdinalIgnoreCase))
                c.Outline(tile.X, tile.Y, tile.W, tile.H, 2f, Gui.AccentHover);
            string kind = isDir ? "DIR" : isScene ? "SCENE" : Extension(entry.Name);
            var kindColor = isDir ? FolderColor : isModel ? ModelColor : isSound ? SoundColor : isScene ? SceneColor : isPrefab ? PrefabColor : Gui.DimColor;
            c.Text(kind, tile.X + (tile.W - c.MeasureText(kind)) / 2f, g.TextTop(tile.Y, tile.H), kindColor);
            string name = g.Fit(SafeLabel.Of(entry.Name, 40), TileSize);
            bool beingCut = _clipboard is { Cut: true } held && string.Equals(held.Path, entry.FullName, StringComparison.OrdinalIgnoreCase);
            c.Text(name, box.X + (TileSize - c.MeasureText(name)) / 2f, g.TextTop(tile.Y + tile.H, NameHeight), beingCut ? Gui.DimColor : Gui.TextColor);

            if (navigateTo is not null)
                break;

            // An opened model lists its animations right after it.
            if (animations.Count > 0 && _expanded.Contains(entry.FullName))
                foreach (string clip in animations)
                {
                    if (placed++ % perRow != 0) g.SameLine();
                    DrawAnimationTile(g, entry.FullName, clip);
                }
        }

        if (navigateTo is not null)
            Navigate(navigateTo);
    }

    // A draggable tile for one animation of a model.
    private void DrawAnimationTile(Gui g, string modelFullPath, string clip)
    {
        var box = g.Reserve(TileSize, TileSize + NameHeight);
        var tile = new Box(box.X, box.Y, TileSize, TileSize);
        g.Behavior($"clip:{modelFullPath}#{clip}", tile, out bool hovered, out _);
        if (g.MousePressed && hovered && _dragClip is null && _dragPath is null)
        {
            _pressedClip = (RelativePath(modelFullPath), clip);
            _pressCursor = g.Mouse;
        }

        var c = g.C;
        c.Rect(tile.X, tile.Y, tile.W, tile.H, hovered ? TileHover : AnimationTile);
        c.Text("ANIM", tile.X + (tile.W - c.MeasureText("ANIM")) / 2f, g.TextTop(tile.Y, tile.H), AnimationColor);
        string name = g.Fit(SafeLabel.Of(clip, 40), TileSize);
        c.Text(name, box.X + (TileSize - c.MeasureText(name)) / 2f, g.TextTop(tile.Y + tile.H, NameHeight), Gui.TextColor);
    }

    // The animations in a model file, read once per refresh.
    private IReadOnlyList<string> AnimationsOf(string fullPath)
    {
        if (!_animationNames.TryGetValue(fullPath, out var names))
            _animationNames[fullPath] = names = ModelLoader.AnimationNames(fullPath);
        return names;
    }

    private void Refresh()
    {
        _dirty = false;
        _animationNames.Clear();
        try
        {
            var dir = new DirectoryInfo(_current);
            var folders = dir.GetDirectories()
                .Where(d => !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Cast<FileSystemInfo>();
            var files = dir.GetFiles()
                .Where(f => !f.Name.StartsWith('.'))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Cast<FileSystemInfo>();
            _entries = folders.Concat(files).ToList();
        }
        catch (IOException)
        {
            _entries = new();
        }
        catch (UnauthorizedAccessException)
        {
            _entries = new();
        }
    }

    private void Navigate(string dir)
    {
        _current = dir;
        _dirty = true;
    }

    private string RelativePath(string full) =>
        Path.GetRelativePath(_root, full).Replace('\\', '/');

    private static string Extension(string name)
    {
        string ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        return ext.Length == 0 ? "FILE" : ext;
    }

    private static float Distance(float2 a, float2 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
