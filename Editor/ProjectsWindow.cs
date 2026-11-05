using System.Reflection;
using Editor.Ui;
using EmpireHub;

namespace Editor;

// File > Change Project: the list of projects, and a form to make a new one the way the Hub does. Picking or making one calls open
// with its folder.
public sealed class ProjectsWindow(string currentProject, Action<string> open)
{
    private static readonly string[] Types = ["3D", "2D"];

    private bool _open;
    private bool _creating;
    private List<ProjectChoice> _projects = new();
    private string _name = "My Game";
    private string _location = "";
    private int _type;
    private string _error = "";

    public bool IsOpen => _open;

    public void Open()
    {
        _open = true;
        _creating = false;
        _error = "";
        _projects = ProjectList.All();
        _location = ProjectList.DefaultLocation(currentProject);
    }

    public void Close() => _open = false;

    public void Draw(Gui g)
    {
        if (!_open) return;

        float w = Math.Min(560f, g.Size.X - 40f), h = Math.Min(520f, g.Size.Y - 80f);
        var rect = new Box((g.Size.X - w) * 0.5f, 60f, w, h);
        if (!g.BeginWindow("ChangeProject", rect, _creating ? "New Project" : "Change Project", WindowFlags.Closable))
            Close();
        else if (_creating)
            DrawNewProject(g);
        else
            DrawProjects(g);
        g.EndWindow();
    }

    private void DrawProjects(Gui g)
    {
        if (g.Button("New Project...", highlighted: true))
        {
            _creating = true;
            _error = "";
        }
        g.Spacing();
        if (_projects.Count == 0)
            g.TextDisabled("No projects yet.");
        for (int i = 0; i < _projects.Count; i++)
        {
            var project = _projects[i];
            bool current = HubSettings.SamePath(project.Folder, currentProject);
            string label = $"{SafeLabel.Of(project.Name, 40)}  ({project.Type}){(current ? "  - open now" : "")}##project{i}";
            if (g.Selectable(label, current, enabled: !current))
                open(project.Folder);
            if (!OperatingSystem.IsIOS())
                g.TextDisabled(SafeLabel.Of(project.Folder, 70));
        }
    }

    private void DrawNewProject(Gui g)
    {
        g.InputText("Name", ref _name, 64);
        if (g.Combo("Type", _type, Types, out int picked)) _type = picked;
        g.TextWrapped(_type == 1
            ? "Laid out flat with an orthographic camera, for games made of pictures (sprites)."
            : "Starts with a sky, a sun and ground.", Gui.DimColor);
        if (!OperatingSystem.IsIOS())
            g.InputText("Location", ref _location, 512);
        g.Spacing();

        string? problem = ProjectTemplate.Validate(_name, _location);
        if (problem is not null || _error.Length > 0)
            g.TextWrapped(problem ?? _error, Gui.ErrorColor);
        if (g.Button("Create", enabled: problem is null, highlighted: true))
            Create();
        g.SameLine();
        if (g.Button("Back"))
            _creating = false;
    }

    private void Create()
    {
        try
        {
            string version = typeof(ProjectsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            string folder = ProjectTemplate.Create(_name, _location, version.Length == 0 ? null : version, is2D: _type == 1);
            ProjectList.Remember(folder);
            open(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _error = $"Could not create the project: {ex.Message}";
        }
    }
}
