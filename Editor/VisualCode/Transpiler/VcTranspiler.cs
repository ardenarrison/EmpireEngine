using VisualCode.Blocks;
using VisualCode.Model;

namespace VisualCode.Transpiler;

public enum DiagnosticSeverity { Warning, Error }

public sealed record Diagnostic(DiagnosticSeverity Severity, string? NodeId, string Message)
{
    public override string ToString() =>
        $"{Severity.ToString().ToLowerInvariant()}: {Message}{(NodeId is null ? "" : $" [block {NodeId}]")}";
}

public sealed record TranspileResult(string Code, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

// Converts a graph into one class deriving from Engine.Core.Scripting.Behaviour.
public sealed class VcTranspiler
{
    private readonly BlockRegistry _registry;

    public VcTranspiler(BlockRegistry? registry = null) => _registry = registry ?? BlockRegistry.Default;

    // The function definitions in the graph, by block id. Reports an empty or repeated name, and a function that calls itself
    // (directly or through others), which would never finish.
    private Dictionary<string, string> FindFunctions(VcGraph graph, Emit emit, List<Diagnostic> diagnostics)
    {
        var byId = new Dictionary<string, string>();
        var seen = new HashSet<string>();
        foreach (var hat in graph.Nodes.Where(n => _registry.Get(n.Type) is { Definition: true }))
        {
            string name = hat.Inputs.TryGetValue("name", out var v) ? v.Literal?.Trim() ?? "" : "";
            string method = Emit.FunctionMethod(name);
            if (name.Length == 0)
                emit.Error(hat, "A function needs a name.");
            else if (!seen.Add(method))
                emit.Error(hat, $"There are two functions named '{name}'.");
            else
            {
                byId[hat.Id] = name;
                emit.Functions.Add(name);
            }
        }

        // Who calls whom
        var calls = new Dictionary<string, HashSet<string>>();
        foreach (var (id, name) in byId)
        {
            var called = new HashSet<string>();
            var visited = new HashSet<string>();
            var pending = new Stack<string?>();
            pending.Push(graph.Find(id)?.Next);
            while (pending.Count > 0)
            {
                if (pending.Pop() is not { } nodeId || !visited.Add(nodeId) || graph.Find(nodeId) is not { } node) continue;
                if (node.Type == "call_function" && node.Inputs.TryGetValue("name", out var target) && target.Literal is { Length: > 0 } t)
                    called.Add(t.Trim());
                pending.Push(node.Next);
                pending.Push(node.Body);
                pending.Push(node.ElseBody);
            }
            calls[name] = called;
        }

        foreach (var name in calls.Keys)
            if (Reaches(name, name, calls, new HashSet<string>()))
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, null,
                    $"The function '{name}' calls itself, directly or through other functions, so it would never finish."));
        return byId;
    }

    // Whether target can be called, directly or through other functions, from the function 'from'.
    private static bool Reaches(string from, string target, Dictionary<string, HashSet<string>> calls, HashSet<string> visited)
    {
        if (!calls.TryGetValue(from, out var called)) return false;
        foreach (var next in called)
            if (next == target || (visited.Add(next) && Reaches(next, target, calls, visited)))
                return true;
        return false;
    }

    public TranspileResult Transpile(VcGraph graph, string className)
    {
        var diagnostics = new List<Diagnostic>();
        var emit = new Emit(graph, _registry, diagnostics);

        var methods = new List<string>();
        var startCalls = new List<string>();
        int scriptIndex = 0;

        var definitions = FindFunctions(graph, emit, diagnostics);

        foreach (var hat in graph.Nodes.Where(n => _registry.Get(n.Type)?.Shape == BlockShape.Hat))
        {
            var spec = _registry.Get(hat.Type)!;
            if (spec.Definition)
            {
                if (definitions.TryGetValue(hat.Id, out var function))
                {
                    methods.Add($"private IEnumerable<Wait> {Emit.FunctionMethod(function)}()");
                    methods.Add("{");
                    methods.AddRange(Code.Indent(spec.Wrap!(hat, emit, emit.Chain(hat.Next))));
                    methods.Add("}");
                    methods.Add(string.Empty);
                }
                continue;
            }

            var name = $"Script{scriptIndex++}";
            var body = spec.Wrap!(hat, emit, emit.Chain(hat.Next));

            methods.Add($"private IEnumerable<Wait> {name}()");
            methods.Add("{");
            methods.AddRange(Code.Indent(body));
            methods.Add("}");
            methods.Add(string.Empty);
            startCalls.Add($"Spawn({name}());");
        }

        var referenced = graph.ReferencedIds();
        foreach (var node in graph.Nodes.Where(n => !referenced.Contains(n.Id)))
        {
            var spec = _registry.Get(node.Type);
            if (spec is null) emit.Error(node, $"Unknown block type '{node.Type}'.");
            else if (spec.Shape != BlockShape.Hat) emit.Warn(node, "Loose block is not attached to a script and was ignored.");
        }

        var fields = new List<string>();
        var seen = new HashSet<string>();
        foreach (var v in graph.Variables)
        {
            if (!seen.Add(v.Name))
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, null, $"Duplicate variable '{v.Name}'."));
                continue;
            }

            var (type, literal) = v.Type switch
            {
                "bool" => ("bool", emit.Literal(InputKind.Bool, v.Default, null, v.Name)),
                "text" => ("string", emit.Literal(InputKind.Text, v.Default, null, v.Name)),
                _ => ("float", emit.Literal(InputKind.Number, v.Default, null, v.Name)),
            };
            fields.Add($"private {type} {Emit.VarField(v.Name)} = {literal};");
        }

        var lines = new List<string>
        {
            "// <auto-generated>",
            $"// Generated by VisualCode from '{Code.Escape(graph.Name)}'. Edit the .visualcode file instead.",
            "// </auto-generated>",
            "#pragma warning disable CS0162, CS0164, CS1998",
            "using System;",
            "using System.Collections.Generic;",
            "using System.Numerics;",
            "using Engine.Core.Scripting;",
            "",
            $"public class {className} : Behaviour",
            "{",
        };

        lines.AddRange(fields.Select(f => "    " + f));
        if (fields.Count > 0) lines.Add("");

        lines.Add("    public override void Start()");
        lines.Add("    {");
        lines.AddRange(startCalls.Select(c => "        " + c));
        lines.Add("    }");
        lines.Add("");
        lines.Add("    public override void Update()");
        lines.Add("    {");
        lines.Add("        TickRoutines();");
        lines.Add("    }");
        lines.Add("");
        lines.AddRange(Code.Indent(methods));
        lines.Add("}");

        return new TranspileResult(string.Join("\n", lines) + "\n", diagnostics);
    }
}
