using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualCode.Model;

// A .visualcode file: a set of block nodes plus the script's variables.
public sealed class VcGraph
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Script";
    public List<VcVariable> Variables { get; set; } = new();
    public List<VcNode> Nodes { get; set; } = new();

    public VcNode? Find(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    // Ids that some other block points at (next, body, else, or a plugged reporter).
    public HashSet<string> ReferencedIds()
    {
        var set = new HashSet<string>();
        foreach (var n in Nodes)
        {
            if (n.Next is not null) set.Add(n.Next);
            if (n.Body is not null) set.Add(n.Body);
            if (n.ElseBody is not null) set.Add(n.ElseBody);
            foreach (var input in n.Inputs.Values)
                if (input.Node is not null) set.Add(input.Node);
        }
        return set;
    }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static VcGraph Load(string json) =>
        JsonSerializer.Deserialize<VcGraph>(json, JsonOptions)
        ?? throw new InvalidDataException("Empty .visualcode file.");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

public sealed class VcVariable
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "number";   // number | bool | text
    public string Default { get; set; } = "0";
}

public sealed class VcNode
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public Dictionary<string, VcInput> Inputs { get; set; } = new();
    public string? Next { get; set; }       // block below this one in the same stack
    public string? Body { get; set; }       // first block inside a C-block mouth
    public string? ElseBody { get; set; }   // first block inside the else mouth
}

// An input holds either a literal (typed text) or a plugged reporter block.
public sealed class VcInput
{
    public string? Literal { get; set; }
    public string? Node { get; set; }
}
