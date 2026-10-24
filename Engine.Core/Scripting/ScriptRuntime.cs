using System.Reflection;
using System.Text;
using Engine.Core.Scene;

namespace Engine.Core.Scripting;

// Creates one script instance per Script component, from the compiled scripts and enabled packages. Compiling happens
// elsewhere. A Behaviour is kept on the Script component; a class that extends Component is put on the object itself,
// right after its Script component, so GetComponent<T>() finds it.
public static class ScriptRuntime
{
    // scripts is the compiled Scripts.dll, or null when the project has no scripts.
    // Returns an error report, or an empty string when every script loaded.
    public static string Load(Engine.Core.Scene.Scene scene, Assembly? scripts) =>
        Load(scene, scripts is null ? Array.Empty<Assembly>() : new[] { scripts });

    // The same with several assemblies: the project's scripts and each enabled package's DLL. A class is looked for in
    // them in order, so a project script wins over a package class with the same name.
    public static string Load(Engine.Core.Scene.Scene scene, IReadOnlyList<Assembly> assemblies)
    {
        Time.Reset();
        Input.Reset();
        GameUi.Reset();
        Engine.Core.Audio.AudioEngine.Reset(scene);
        SceneManager.World = scene;
        _assemblies = assemblies.ToList();
        return Attach(scene.All.ToList());
    }

    // The compiled scripts and packages, kept so objects that arrive later (another scene loaded while playing) get
    // their scripts too.
    private static List<Assembly> _assemblies = new();

    // The script class with this name, from the first assembly that has it.
    private static Type? FindType(string name)
    {
        foreach (var assembly in _assemblies)
            if (assembly.GetType(name) is { } type)
                return type;
        return null;
    }

    // Creates a script instance for each Script component on the objects. Start runs on the next frame, unless a save
    // game says it already ran. Returns an error report, or an empty string when every script loaded.
    internal static string Attach(IReadOnlyList<SceneObject> objects)
    {
        var attached = objects
            .SelectMany(o => o.Components.OfType<ScriptComponent>().Select(c => (Obj: o, Comp: c)))
            .ToList();
        foreach (var (obj, comp) in attached)
        {
            comp.Instance = null;
            if (comp.ComponentInstance is { } old) obj.Components.Remove(old);
            comp.ComponentInstance = null;
            comp.ComponentStopped = false;
            comp.Started = comp.RestoredStarted;
            comp.RestoredStarted = false;
        }
        if (attached.Count == 0)
            return "";
        if (_assemblies.Count == 0)
            return "The scene has scripts, but no compiled scripts were loaded.";

        var problems = new StringBuilder();
        foreach (var (obj, comp) in attached)
        {
            var type = FindType(comp.ScriptName);
            bool isBehaviour = type is not null && typeof(Behaviour).IsAssignableFrom(type) && !type.IsAbstract;
            bool isComponent = type is not null && typeof(Component).IsAssignableFrom(type) && !type.IsAbstract &&
                               type.Assembly != typeof(Component).Assembly;
            if (!isBehaviour && !isComponent)
            {
                problems.AppendLine($"'{obj.Name}': no script or enabled package has a class named {comp.ScriptName} that " +
                                    "derives from Behaviour or Component. (Was its package uninstalled?)");
                continue;
            }

            object instance = Activator.CreateInstance(type!)!;
            // Inspector values go in before Start runs. A value that no longer fits is reported but does not stop play.
            // Object fields point at objects anywhere in the scene that is playing, found by id.
            var fieldProblems = ScriptFields.Apply(instance, comp.Fields, id => SceneManager.World?.Find(id));
            if (fieldProblems.Length > 0)
                Console.Write($"'{obj.Name}': {fieldProblems}");

            if (instance is Behaviour behaviour)
            {
                behaviour.Object = obj;
                comp.Instance = behaviour;
            }
            else
            {
                var component = (Component)instance;
                component.Object = obj;
                int at = obj.Components.IndexOf(comp);
                obj.Components.Insert(at < 0 ? obj.Components.Count : at + 1, component);
                comp.ComponentInstance = component;
            }
        }
        return problems.ToString();
    }

    // Calls OnGameLoaded on every script that was already started when the game was saved.
    internal static void NotifyLoaded(IEnumerable<SceneObject> objects)
    {
        foreach (var obj in objects)
            foreach (var comp in obj.Components.OfType<ScriptComponent>())
            {
                if (comp.Instance is not { } script || !comp.Started)
                    continue;
                try
                {
                    script.OnGameLoaded();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Script error in {comp.ScriptName}.OnGameLoaded on '{obj.Name}': {ex.Message}. The script is stopped.");
                    comp.Instance = null;
                }
            }
    }

    // Tells every script on obj that it started (enter) or stopped touching other.
    internal static void Collision(SceneObject obj, SceneObject other, bool enter)
    {
        foreach (var comp in obj.Components.OfType<ScriptComponent>())
        {
            if (comp.Instance is not { } script)
                continue;
            try
            {
                if (enter) script.OnCollisionEnter(other);
                else script.OnCollisionExit(other);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Script error in {comp.ScriptName}.{(enter ? "OnCollisionEnter" : "OnCollisionExit")} " +
                                  $"on '{obj.Name}': {ex.Message}. The script is stopped.");
                comp.Instance = null;
            }
        }
    }

    // Runs Start (once) and then Update for every script. Called once per frame while the scene plays.
    internal static void Run(Engine.Core.Scene.Scene scene)
    {
        foreach (var obj in scene.All)
            foreach (var comp in obj.Components.OfType<ScriptComponent>().ToList())
            {
                if (comp.Instance is null && (comp.ComponentInstance is null || comp.ComponentStopped))
                    continue;
                try
                {
                    if (!comp.Started)
                    {
                        comp.Started = true;
                        if (comp.Instance is { } script) script.Start();
                        else comp.ComponentInstance!.Start();
                    }
                    if (comp.Instance is { } running) running.Update();
                    else comp.ComponentInstance?.Update();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Script error in {comp.ScriptName} on '{obj.Name}': {ex.Message}. " +
                                      "The script is stopped.");
                    comp.Instance = null;
                    comp.ComponentStopped = true;   // the component stays on the object, but no longer runs
                }
            }
    }
}
