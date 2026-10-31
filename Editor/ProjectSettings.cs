using System.Text.Json;
using System.Text.Json.Nodes;
using Engine.Core.Scene;

namespace Editor;

// Settings that belong to a project, kept in project-settings.json at the project's root (next to project.empire) so they travel with
// the project and with version control. The editor owns this file; the Hub never touches it. Anything in the file the editor doesn't
// know about is kept when it saves, so a newer editor's settings survive an older one.
public sealed class ProjectSettings
{
    public const string FileName = "project-settings.json";

    // The scene view's far clip plane: how far from the editor camera the scene is drawn, in world units. This is only the editor's
    // own camera while editing; cameras in the scene have their own Far, and games use those.
    public const float DefaultFarClipPlane = 500f;
    public const float MinFarClipPlane = 10f;
    public const float MaxFarClipPlane = 100_000f;

    // The near clip plane: how close to the editor camera things can be and still be drawn, in world units. It is at most MaxNearClipPlane,
    // and never more than half the far clip plane, so the two always leave a usable depth range.
    public const float DefaultNearClipPlane = 0.1f;
    public const float MinNearClipPlane = 0.01f;
    public const float MaxNearClipPlane = 100f;

    // Whether object positions keep float (32-bit) or double (64-bit) precision. Float is what every project used before.
    public PositionType PositionType { get; set; } = PositionType.Float;

    // Grid steps for snapping with the move and scale handles (hold Ctrl while dragging): world units, and scale.
    public const float DefaultMoveSnap = 0.5f;
    public const float DefaultScaleSnap = 0.1f;
    public const float MinSnap = 0.001f;
    public const float MaxSnap = 1000f;

    private float _moveSnap = DefaultMoveSnap;
    private float _scaleSnap = DefaultScaleSnap;

    public float MoveSnap { get => _moveSnap; set => _moveSnap = ClampSnap(value, DefaultMoveSnap); }
    public float ScaleSnap { get => _scaleSnap; set => _scaleSnap = ClampSnap(value, DefaultScaleSnap); }

    public static float ClampSnap(float value, float fallback) =>
        float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Clamp(value, MinSnap, MaxSnap);

    // Whether the scene view shows frames per second in its top-right corner (Window > FPS Counter).
    public bool ShowFpsCounter { get; set; }

    private float _editorFarClipPlane = DefaultFarClipPlane;
    private float _editorNearClipPlane = DefaultNearClipPlane;

    public float EditorFarClipPlane
    {
        get => _editorFarClipPlane;
        set
        {
            _editorFarClipPlane = Clamp(value);
            _editorNearClipPlane = ClampNear(_editorNearClipPlane, _editorFarClipPlane);   // a nearer far plane can pull the near plane in with it
        }
    }

    public float EditorNearClipPlane
    {
        get => _editorNearClipPlane;
        set => _editorNearClipPlane = ClampNear(value, _editorFarClipPlane);
    }

    // The largest near clip plane the given far clip plane allows.
    public static float MaxNearFor(float far) => Math.Min(MaxNearClipPlane, far * 0.5f);

    public static float ClampNear(float value, float far) =>
        float.IsNaN(value) || float.IsInfinity(value) ? Math.Min(DefaultNearClipPlane, MaxNearFor(far)) : Math.Clamp(value, MinNearClipPlane, MaxNearFor(far));

    // A value outside the range is brought into it, and something that isn't a number becomes the default.
    public static float Clamp(float value) =>
        float.IsNaN(value) || float.IsInfinity(value) ? DefaultFarClipPlane : Math.Clamp(value, MinFarClipPlane, MaxFarClipPlane);

    // Reads the project's settings. A missing, empty or unreadable file gives the defaults (and is left alone until something is saved).
    public static ProjectSettings Load(string projectFolder)
    {
        var settings = new ProjectSettings();
        try
        {
            string file = Path.Combine(projectFolder, FileName);
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject root)
            {
                if (root["positionType"] is JsonValue kind && kind.TryGetValue(out string? name) &&
                    Enum.TryParse(name, ignoreCase: true, out PositionType type))
                    settings.PositionType = type;
                if (root["moveSnap"] is JsonValue moveSnap && moveSnap.TryGetValue(out double moveSnapValue))
                    settings.MoveSnap = (float)moveSnapValue;
                if (root["scaleSnap"] is JsonValue scaleSnap && scaleSnap.TryGetValue(out double scaleSnapValue))
                    settings.ScaleSnap = (float)scaleSnapValue;
                if (root["showFpsCounter"] is JsonValue fps && fps.TryGetValue(out bool showFps))
                    settings.ShowFpsCounter = showFps;
                if (root["editorFarClipPlane"] is JsonValue far && far.TryGetValue(out double farValue))
                    settings.EditorFarClipPlane = (float)farValue;   // first, because the near plane's limit depends on it
                if (root["editorNearClipPlane"] is JsonValue near && near.TryGetValue(out double nearValue))
                    settings.EditorNearClipPlane = (float)nearValue;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        return settings;
    }

    // Writes the settings, keeping other fields already in the file. The file is replaced in one step, so it is never half written.
    // Throws IOException or UnauthorizedAccessException when the folder can't be written.
    public void Save(string projectFolder)
    {
        string file = Path.Combine(projectFolder, FileName);
        JsonObject root = new();
        try
        {
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject existing) root = existing;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }   // a broken file is replaced

        root["positionType"] = PositionType.ToString().ToLowerInvariant();
        root["moveSnap"] = MoveSnap;
        root["scaleSnap"] = ScaleSnap;
        root["showFpsCounter"] = ShowFpsCounter;
        root["editorFarClipPlane"] = EditorFarClipPlane;
        root["editorNearClipPlane"] = EditorNearClipPlane;
        string temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
