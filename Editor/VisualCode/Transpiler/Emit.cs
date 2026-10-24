using System.Globalization;
using System.Text.RegularExpressions;
using VisualCode.Blocks;
using VisualCode.Model;

namespace VisualCode.Transpiler;

// Passed to every block generator. Turns inputs into C# expressions and stacks into statements.
public sealed class Emit
{
    private readonly VcGraph _graph;
    private readonly BlockRegistry _registry;
    private readonly List<Diagnostic> _diagnostics;
    private int _counter;

    public Emit(VcGraph graph, BlockRegistry registry, List<Diagnostic> diagnostics)
    {
        _graph = graph;
        _registry = registry;
        _diagnostics = diagnostics;
    }

    public string Unique(string prefix) => prefix + (++_counter);

    public void Warn(VcNode? node, string message) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, node?.Id, message));

    public void Error(VcNode? node, string message) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, node?.Id, message));

    public static string VarField(string name) => "_v_" + Regex.Replace(name, "[^A-Za-z0-9_]", "_");

    public static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    // The C# expression for one input of a block: either a plugged reporter or a literal.
    public string Value(VcNode owner, string input)
    {
        var spec = _registry.Get(owner.Type);
        var inputSpec = spec?.Inputs.FirstOrDefault(i => i.Name == input);
        var kind = inputSpec?.Kind ?? InputKind.Number;
        var fallback = inputSpec?.Default ?? "0";

        if (!owner.Inputs.TryGetValue(input, out var value))
            return Literal(kind, fallback, owner, input);

        if (value.Node is { } childId)
        {
            var child = _graph.Find(childId);
            var childSpec = child is null ? null : _registry.Get(child.Type);
            if (child is null || childSpec?.Expr is null)
            {
                Error(owner, $"Input '{input}' is plugged with a block that is missing or is not a reporter.");
                return Literal(kind, fallback, owner, input);
            }
            return childSpec.Expr(child, this);
        }

        return Literal(kind, value.Literal ?? fallback, owner, input);
    }

    public string Literal(InputKind kind, string text, VcNode? owner, string input)
    {
        switch (kind)
        {
            case InputKind.Number:
                return Number(text, owner, input);

            case InputKind.Any:
                if (bool.TryParse(text, out var anyFlag)) return anyFlag ? "true" : "false";
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    return Number(text, owner, input);
                return Quote(text);

            case InputKind.Bool:
                return bool.TryParse(text, out var flag) && flag ? "true" : "false";

            case InputKind.Variable:
                if (!_graph.Variables.Any(v => v.Name == text))
                    Warn(owner, $"Variable '{text}' is not declared.");
                return VarField(text);

            default: // Text, Key, Choice
                return Quote(text);
        }
    }

    private string Number(string text, VcNode? owner, string input)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            return d.ToString("R", CultureInfo.InvariantCulture) + "f";

        Warn(owner, $"'{text}' is not a number for input '{input}'; using 0.");
        return "0f";
    }

    // Generates statements for a chain of stack blocks starting at startId.
    public List<string> Chain(string? startId)
    {
        var lines = new List<string>();
        var visited = new HashSet<string>();
        var id = startId;

        while (id is not null)
        {
            if (!visited.Add(id))
            {
                Error(null, $"Cycle detected at block '{id}'.");
                break;
            }

            var node = _graph.Find(id);
            if (node is null)
            {
                Error(null, $"Reference to missing block '{id}'.");
                break;
            }

            var spec = _registry.Get(node.Type);
            if (spec is null)
            {
                Error(node, $"Unknown block type '{node.Type}'.");
                break;
            }

            if (spec.Shape is BlockShape.Reporter or BlockShape.Hat || spec.Stmt is null)
            {
                Error(node, $"'{node.Type}' cannot be placed inside a stack.");
                break;
            }

            lines.AddRange(spec.Stmt(node, this));
            id = node.Next;
        }

        return lines;
    }
}
