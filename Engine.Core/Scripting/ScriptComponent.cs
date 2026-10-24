using Engine.Core.Scene;

namespace Engine.Core.Scripting;

// Attaches a script to an object: a Behaviour from the project's Scripts folder, or a component written in a script or
// an enabled package (a class that extends Component). This is what the scene keeps; when play starts the class is
// created from the compiled scripts.
public sealed class ScriptComponent : Component
{
    public string ScriptName { get; set; } = "";   // the class name (for a Behaviour, also its file name without .cs)

    // Values set in the Inspector for the script's public fields, by field name, as JSON text ("5", "\"hi\"", "[1,2]").
    // Fields not listed keep the default the script's code gives them.
    public Dictionary<string, string> Fields { get; set; } = new();

    // Created when play starts. Null until then.
    public Behaviour? Instance { get; internal set; }

    // For a class that extends Component: the component made when play starts. It is also on the object, after this,
    // so GetComponent finds it.
    public Component? ComponentInstance { get; internal set; }
    internal bool ComponentStopped { get; set; }   // its Start or Update threw, so it no longer runs
    internal bool Started { get; set; }
    internal bool RestoredStarted { get; set; }   // set when a save game says Start already ran, so it is not run again
}
