using Brutal.Numerics;
using Editor.Ui;
using Engine.Core.Assets;
using Engine.Core.Audio;

namespace Editor;

// Docked along the bottom of the editor, full window width. Browses the project folder and its subfolders.
// Model files (.glb, .gltf, .fbx, .obj) can be dragged from here and dropped on the scene to place them. Sound files
// (.mp3, .wav) can be dragged onto an Audio Source in the Inspector, or onto the scene to make an object that plays them.
// Models that contain animations show a small arrow: click it to list the animations right after the model, and drag
// one onto the Clip field of an Animator in the Inspector.
// Click a scene to see and change its settings (such as the shadow resolution) in the Inspector.
// Right-click anywhere in the panel and choose Create Scene to make a new scene file in the folder being shown.
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

    private bool _creatingScene;        // the Create Scene dialog is open
    private string _newSceneName = "";
    private string _createdMessage = "";   // what the last Create Scene made, shown in the panel until the next action

    public AssetPanel(string projectRoot)
    {
        _root = Path.GetFullPath(projectRoot);
        _current = _root;
    }

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

    public void Draw(Gui g)
    {
        g.BeginWindow("Assets", new Box(0f, g.Size.Y - Height, g.Size.X, Height), "Assets");
        DrawNavigation(g);
        g.Separator();
        DrawTiles(g);

        // Right-clicking anywhere in the panel offers to make a scene in the folder being shown.
        if (g.BeginContextMenu("assets", width: 180f))
        {
            if (g.MenuItem("Create Scene"))
            {
                _creatingScene = true;
                _newSceneName = "";
                g.FocusNextInput();
            }
            g.EndPopup();
        }
        g.EndWindow();

        DrawCreateSceneDialog(g);
        UpdateDrag(g);
    }

    // Closes the Create Scene dialog if it is open. Returns true when it was, so Escape closes only that.
    public bool CloseDialog()
    {
        if (!_creatingScene) return false;
        _creatingScene = false;
        return true;
    }

    // Asks for a name and writes a new scene file in the folder being shown.
    private void DrawCreateSceneDialog(Gui g)
    {
        if (!_creatingScene) return;

        var rect = new Box((g.Size.X - 460f) / 2f, g.Size.Y * 0.28f, 460f, 230f);
        if (!g.BeginWindow("CreateScene", rect, "Create Scene", WindowFlags.Modal | WindowFlags.Closable | WindowFlags.NoScroll))
            _creatingScene = false;

        string rel = Path.GetRelativePath(_root, _current).Replace('\\', '/');
        string where = rel == "." ? "Project folder" : rel;
        g.TextDisabled("In: " + SafeLabel.Of(where, 60));
        g.InputText("Name", ref _newSceneName, 64);

        string? problem = _newSceneName.Length == 0 ? null : SceneFiles.Validate(_current, _newSceneName);
        if (problem is not null)
            g.TextWrapped(problem, Gui.ErrorColor);
        else
        {
            // How a script loads it: scenes in the Scenes folder by name, anywhere else by path from the project folder.
            string name = SceneFiles.CleanName(_newSceneName);
            string shown = name.Length == 0 ? "Name" : SafeLabel.Of(name, 40);
            bool inScenes = rel == "Scenes" || rel.StartsWith("Scenes/", StringComparison.Ordinal);
            string call = inScenes
                ? $"SceneManager.LoadScene(\"{(rel.Length > 7 ? rel[7..] + "/" : "")}{shown}\")"
                : $"SceneManager.LoadScene(\"{(rel == "." ? "" : rel + "/")}{shown}.empirescene\")";
            g.TextWrapped("Load it from a script with " + call, Gui.DimColor);
        }

        g.Spacing();
        bool valid = _newSceneName.Length > 0 && problem is null;
        if ((g.Button("Create", enabled: valid, highlighted: valid) || (g.EnterPressed && valid)) && valid)
        {
            try
            {
                string path = SceneFiles.Create(_current, _newSceneName);
                _createdMessage = $"Created {RelativePath(path)}";
                Console.WriteLine(_createdMessage + ".");
                _dirty = true;
                _creatingScene = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _createdMessage = $"Could not create the scene: {ex.Message}";
                Console.WriteLine(_createdMessage);
                _creatingScene = false;
            }
        }
        g.SameLine();
        if (g.Button("Cancel"))
            _creatingScene = false;
        g.EndWindow();
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
        if (_createdMessage.Length > 0)
        {
            g.SameLine();
            g.TextDisabled(_createdMessage);
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
            bool isScene = !isDir && entry.Name.EndsWith(SceneFiles.Extension, StringComparison.OrdinalIgnoreCase);
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

            if (isDir && clicked)
                navigateTo = entry.FullName;
            if (isScene && clicked)
                _clickedScene = RelativePath(entry.FullName);
            if ((isModel || isSound) && g.MousePressed && hovered && !overArrow && _dragPath is null)
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
            var kindColor = isDir ? FolderColor : isModel ? ModelColor : isSound ? SoundColor : isScene ? SceneColor : Gui.DimColor;
            c.Text(kind, tile.X + (tile.W - c.MeasureText(kind)) / 2f, g.TextTop(tile.Y, tile.H), kindColor);
            string name = g.Fit(SafeLabel.Of(entry.Name, 40), TileSize);
            c.Text(name, box.X + (TileSize - c.MeasureText(name)) / 2f, g.TextTop(tile.Y + tile.H, NameHeight), Gui.TextColor);

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
