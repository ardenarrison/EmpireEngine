using VisualCode.Model;
using VisualCode.Transpiler;

namespace VisualCode.Blocks;

public enum BlockShape { Hat, Stack, CBlock, IfElse, Reporter }

public enum InputKind { Number, Any, Text, Bool, Key, Choice, Variable }

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

    public Func<VcNode, Emit, List<string>>? Stmt { get; init; }                       // stack blocks
    public Func<VcNode, Emit, string>? Expr { get; init; }                             // reporters
    public Func<VcNode, Emit, List<string>, List<string>>? Wrap { get; init; }         // hats: wraps the script body
}
