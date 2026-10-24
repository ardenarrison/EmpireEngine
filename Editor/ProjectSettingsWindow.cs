using Editor.Ui;

namespace Editor;

// File > Project Settings: settings that belong to the project and are kept in its project-settings.json.
// For now that is the editor's near and far clip planes.
public sealed class ProjectSettingsWindow(ProjectSettings settings, EditorCamera camera, string projectFolder)
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
        if (!g.BeginWindow("ProjectSettings", rect, "Project Settings", WindowFlags.Closable | WindowFlags.NoScroll))
        {
            Close();
            g.EndWindow();
            return;
        }

        g.Text("Editor Far Clip Plane");
        float far = settings.EditorFarClipPlane;
        if (g.DragFloat("##far", ref far, 25f, ProjectSettings.MinFarClipPlane, ProjectSettings.MaxFarClipPlane))
        {
            settings.EditorFarClipPlane = far;
            Apply();
        }
        g.TextWrapped($"How far from the editor's camera the scene is drawn, in world units ({ProjectSettings.MinFarClipPlane:0} to {ProjectSettings.MaxFarClipPlane:0}, " +
                      $"usually {ProjectSettings.DefaultFarClipPlane:0}). Raise it to see distant objects while you edit; lower it for finer depth detail close up.", Gui.DimColor);
        g.Spacing();

        g.Text("Editor Near Clip Plane");
        float near = settings.EditorNearClipPlane;
        if (g.DragFloat("##near", ref near, 0.01f, ProjectSettings.MinNearClipPlane, ProjectSettings.MaxNearFor(settings.EditorFarClipPlane)))
        {
            settings.EditorNearClipPlane = near;
            Apply();
        }
        g.TextWrapped($"How close to the editor's camera things can be and still be drawn ({ProjectSettings.MinNearClipPlane:0.##} to {ProjectSettings.MaxNearClipPlane:0}, " +
                      $"usually {ProjectSettings.DefaultNearClipPlane:0.0}). Lower it to look at small things up close without them being cut off; " +
                      "raise it for finer depth detail far away. It is never more than half the far plane.", Gui.DimColor);
        g.Spacing();

        g.TextWrapped("Drag a number, or click it to type one. These only change the camera you fly around the scene with while editing. " +
                      "The cameras in your scene have their own Near and Far, which is what the game uses.", Gui.DimColor);
        g.Spacing();
        bool usual = settings.EditorFarClipPlane == ProjectSettings.DefaultFarClipPlane && settings.EditorNearClipPlane == ProjectSettings.DefaultNearClipPlane;
        if (g.Button("Reset both to the usual values", enabled: !usual))
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
