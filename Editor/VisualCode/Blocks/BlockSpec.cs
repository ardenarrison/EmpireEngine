using VisualCode.Model;
using VisualCode.Transpiler;

namespace VisualCode.Blocks;

public enum BlockShape { Hat, Stack, CBlock, IfElse, Reporter }

public enum InputKind { Number, Any, Text, Bool, Key, Choice, Variable, Object, Function, Tag }   // Function: the name of a function defined in the script, picked from a list; Object: the name of an object in the scene, picked from a list; Tag: a tag used in the scene, picked from a list

public sealed record InputSpec(string Name, InputKind Kind, string Default = "0", string[]? Options = null);

// Describes one block type and how it becomes C#.
public sealed class BlockSpec
{
    public required string Type { get; init; }
    public required string Category { get; init; }
    public required string Label { get; init; }          // e.g. "move {distance} units forward"
    public required BlockShape Shape { get; init; }
    public IReadOnlyList<InputSpec> Inputs { get; init; } = Array.Empty<InputSpec>();
    public string Returns { get; init; } = "any";
    public bool Definition { get; init; }                // a hat that defines a function instead of starting a script
    public bool OnDestroy { get; init; }                 // a hat whose script runs when the object is destroyed, not when the scene starts

    public Func<VcNode, Emit, List<string>>? Stmt { get; init; }                       // stack blocks
    public Func<VcNode, Emit, string>? Expr { get; init; }                             // reporters
    public Func<VcNode, Emit, List<string>, List<string>>? Wrap { get; init; }         // hats: wraps the script body
}
