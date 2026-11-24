using Editor.Ui;
using Engine.Core.Rendering;
using Engine.Core.Scene;

namespace Editor;

// File > Project Settings: settings that belong to the project and are kept in its project-settings.json.
// That is the position type (float or double), the graphics API on a Mac, the snapping steps and the editor's near clip plane (and far clip plane, in a 2D project).
// drawingWith is the graphics API this editor session draws with, which only changes when the project is opened again.
public sealed class ProjectSettingsWindow(ProjectSettings settings, EditorCamera camera, string projectFolder, Action<PositionType> positionTypeChanged,
    Func<GraphicsApi> drawingWith)
{
    private bool _open;
    private bool _dirty;          // changed but not saved yet: saved when the mouse is let go (not on every step of a drag) and when the window closes
    private string _message = "";

    public bool IsOpen => _open;

    public void Open() { _open = true; _message = ""; }

    public void Close()
    {
        _open = false;
        Save();
    }

    public void Draw(Gui g)
    {
        if (!_open) return;

        float w = Math.Min(560f, g.Size.X - 40f), h = Math.Min(560f, g.Size.Y - 80f);
        var rect = new Box((g.Size.X - w) * 0.5f, 70f, w, h);
        if (!g.BeginWindow("ProjectSettings", rect, "Project Settings", WindowFlags.Closable))
        {
            Close();
            g.EndWindow();
            return;
        }

        var type = settings.PositionType;
        if (g.Combo("Position Type", ref type))
        {
            settings.PositionType = type;
            positionTypeChanged(type);
            _dirty = true;
        }
        g.TextWrapped("Float keeps about 7 digits of precision, which is plenty for most games. Double keeps about 16, for very large worlds: far from the " +
                      "origin an object can still move in tiny steps, and small movements added up over time stay exact. Double positions are edited in the Inspector " +
                      "and read in scripts as transform.positionD. Physics, rendering and audio still work in floats. Going back to Float rounds every position now in the scene.", Gui.DimColor);
        g.Spacing();

        var graphics = settings.MacGraphics;
        if (g.Combo("Graphics on Mac", ref graphics))
        {
            settings.MacGraphics = graphics;
            _dirty = true;
        }
        g.TextWrapped("How the editor and the project's Mac games draw on a Mac. Vulkan goes through MoltenVK, which turns it into Metal, and is the " +
                      "same renderer as on Windows and Linux. Metal draws with Apple's own API directly (experimental). Both draw the same picture; " +
                      "scripts that draw with Vulkan themselves (IVulkanRender) only run with Vulkan. Games for Windows and Linux always use Vulkan.", Gui.DimColor);
        if (OperatingSystem.IsMacOS() && GraphicsApis.Resolve(settings.MacGraphics) != drawingWith())
            g.TextWrapped($"The editor is drawing with {drawingWith()} until you open the project again (File > Change Project, or close and " +
                          "reopen it). Builds use the new choice right away.", Gui.AccentColor);
        g.Spacing();

        g.Text("Move Snap");
        float moveSnap = settings.MoveSnap;
        if (g.DragFloat("##moveSnap", ref moveSnap, 0.01f, ProjectSettings.MinSnap, ProjectSettings.MaxSnap))
        {
            settings.MoveSnap = moveSnap;
            _dirty = true;
        }
        g.Text("Scale Snap");
        float scaleSnap = settings.ScaleSnap;
        if (g.DragFloat("##scaleSnap", ref scaleSnap, 0.01f, ProjectSettings.MinSnap, ProjectSettings.MaxSnap))
        {
            settings.ScaleSnap = scaleSnap;
            _dirty = true;
        }
        g.TextWrapped($"Hold Ctrl while dragging a move or scale handle to snap. A move puts the object on a grid of Move Snap world units along " +
                      $"that axis (usually {ProjectSettings.DefaultMoveSnap:0.0}); a scale rounds to a multiple of Scale Snap (usually {ProjectSettings.DefaultScaleSnap:0.0}), " +
                      "and the center box scales in steps of it (0.1 is 10% at a time).", Gui.DimColor);
        g.Spacing();

        // The 3D view sees forever, so only a 2D project's orthographic view has a far clip plane.
        if (camera.Is2D)
        {
            g.Text("Editor Far Clip Plane");
            float far = settings.EditorFarClipPlane;
            if (g.DragFloat("##far", ref far, 25f, ProjectSettings.MinFarClipPlane, ProjectSettings.MaxFarClipPlane))
            {
                settings.EditorFarClipPlane = far;
                Apply();
            }
            g.TextWrapped($"How far from the editor's camera the scene is drawn, in world units ({ProjectSettings.MinFarClipPlane:0} to {ProjectSettings.MaxFarClipPlane:0}, " +
                          $"usually {ProjectSettings.DefaultFarClipPlane:0}). Raise it to see objects farther back in Z while you edit.", Gui.DimColor);
            g.Spacing();
        }

        g.Text("Editor Near Clip Plane");
        float near = settings.EditorNearClipPlane;
        if (g.DragFloat("##near", ref near, 0.01f, ProjectSettings.MinNearClipPlane, ProjectSettings.MaxNearFor(settings.EditorFarClipPlane)))
        {
            settings.EditorNearClipPlane = near;
            Apply();
        }
        g.TextWrapped($"How close to the editor's camera things can be and still be drawn ({ProjectSettings.MinNearClipPlane:0.##} to {ProjectSettings.MaxNearClipPlane:0}, " +
                      $"usually {ProjectSettings.DefaultNearClipPlane:0.0}). Lower it to look at small things up close without them being cut off; " +
                      (camera.Is2D ? "raise it for finer depth detail. It is never more than half the far plane." : "raise it for finer depth detail."), Gui.DimColor);
        g.Spacing();

        g.TextWrapped("Drag a number, or click it to type one. These only change the camera you fly around the scene with while editing. " +
                      "The cameras in your scene have their own settings, which is what the game uses. " +
                      (camera.Is2D ? "" : "The 3D view has no far clip plane: it shows things however far away they are."), Gui.DimColor);
        g.Spacing();
        bool usual = (!camera.Is2D || settings.EditorFarClipPlane == ProjectSettings.DefaultFarClipPlane) &&
                     settings.EditorNearClipPlane == ProjectSettings.DefaultNearClipPlane;
        if (g.Button(camera.Is2D ? "Reset both to the usual values" : "Reset to the usual value", enabled: !usual))
        {
            settings.EditorFarClipPlane = ProjectSettings.DefaultFarClipPlane;
            settings.EditorNearClipPlane = ProjectSettings.DefaultNearClipPlane;
            Apply();
        }
        g.Separator();
        g.TextDisabled("Saved in this project's project-settings.json.");
        if (_message.Length > 0) g.TextWrapped(_message, Gui.ErrorColor);

        if (_dirty && !g.MouseDown && !g.IsTyping) Save();
        g.EndWindow();
    }

    // Gives the editor camera the settings as they are now, and marks them to be saved.
    private void Apply()
    {
        camera.Far = settings.EditorFarClipPlane;
        camera.Near = settings.EditorNearClipPlane;
        _dirty = true;
    }

    private void Save()
    {
        if (!_dirty) return;
        try
        {
            settings.Save(projectFolder);
            _dirty = false;
            _message = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dirty = false;   // don't try again every frame; the next change tries again
            _message = "Couldn't save the project's settings: " + ex.Message;
        }
    }
}
