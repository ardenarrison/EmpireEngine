using Editor.Ui;
using Engine.Core.Scene;
using Engine.Core.Scripting;
using Engine.Scripting.Build;

namespace Editor;

// File > Package Manager: the packages that come with the editor, each with an Install button for this project.
// A package is a set of scripts the editor compiles into the project (Packages/EmpirePackage.<Id>.dll); its components
// then appear in Add Component, and builds include it. No package is installed until you install it.
public sealed class PackageManagerWindow
{
    private readonly string _projectRoot;
    private bool _open;
    private IReadOnlyList<PackageInfo> _available = Array.Empty<PackageInfo>();
    private List<string> _enabled = new();
    private string _message = "";

    public PackageManagerWindow(string projectRoot) => _projectRoot = projectRoot;

    // Raised after a package is installed or uninstalled, so the editor can compile again.
    public event Action? Changed;

    public bool IsOpen => _open;
    public void Close() => _open = false;

    public void Open()
    {
        _open = true;
        Reload();
    }

    private void Reload()
    {
        _available = Packages.Available();
        _enabled = Packages.Enabled(_projectRoot);
    }

    // classes and errors come from the editor's script compile, to show what each package adds and whether it built.
    public void Draw(Gui g, Scene scene, IEnumerable<ScriptClass> classes, string compileErrors, bool compiling)
    {
        if (!_open) return;

        float w = Math.Min(620f, g.Size.X - 40f), h = Math.Min(520f, g.Size.Y - 80f);
        var rect = new Box((g.Size.X - w) * 0.5f, 60f, w, h);
        if (!g.BeginWindow("PackageManager", rect, "Package Manager", WindowFlags.Closable))
            _open = false;

        g.TextWrapped("Packages add features to this project as ready-made scripts and components. An installed " +
                      "package is compiled into the project's Packages folder and included when the game is built.",
                      Gui.DimColor);
        g.Separator();

        if (_available.Count == 0)
            g.TextWrapped($"No packages were found in {Packages.DefaultRoot}.", Gui.ErrorColor);

        var classList = classes.ToList();
        foreach (var package in _available)
        {
            bool enabled = _enabled.Contains(package.Id);
            g.PushId("package:" + package.Id);
            g.Text($"{package.Name}  {package.Version}");
            g.TextWrapped(package.Description, Gui.DimColor);

            if (enabled)
            {
                var components = classList.Where(c => c.Package == package.Id && c.IsComponent).Select(c => c.Name).ToList();
                g.TextDisabled(compiling && components.Count == 0
                    ? "Compiling..."
                    : components.Count > 0 ? "Installed. Adds: " + string.Join(", ", components) : "Installed.");
            }

            if (g.Button(enabled ? "Uninstall##toggle" : "Install##toggle", highlighted: !enabled))
            {
                int users = enabled ? CountUsers(scene, classList, package.Id) : 0;
                Packages.SetEnabled(_projectRoot, package.Id, !enabled);
                Reload();
                _message = enabled
                    ? $"{package.Name} is uninstalled." + (users > 0
                        ? $" {users} component(s) in this scene use it; they keep their settings and work again if it is installed again."
                        : "")
                    : $"{package.Name} is installed. Its components are in Add Component once it has compiled.";
                Changed?.Invoke();
            }
            g.Separator();
            g.PopId();
        }

        if (_message.Length > 0)
            g.TextWrapped(_message, Gui.DimColor);
        if (compileErrors.Length > 0)
            g.TextWrapped(compileErrors, Gui.ErrorColor);
        g.EndWindow();
    }

    // How many components in the scene come from this package.
    private static int CountUsers(Scene scene, List<ScriptClass> classes, string package)
    {
        var names = classes.Where(c => c.Package == package).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return scene.All.Sum(o => o.Components.OfType<ScriptComponent>().Count(s => names.Contains(s.ScriptName)));
    }
}
