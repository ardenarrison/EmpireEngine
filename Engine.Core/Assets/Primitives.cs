using System.Numerics;

namespace Engine.Core.Assets;

// Models that come with the engine, so they need no file in the project: a MeshRenderer's Model is "builtin:Cube" and so on. They
// load the same way in the editor, in built games and on iPad. The shapes are made here, one unit across and centered on the
// object (the capsule is two tall); the Empire crown is the model file embedded in this assembly.
public static class Primitives
{
    public const string Prefix = "builtin:";
    public const string Cube = Prefix + "Cube";
    public const string Sphere = Prefix + "Sphere";
    public const string Capsule = Prefix + "Capsule";
    public const string Cone = Prefix + "Cone";
    public const string Empire = Prefix + "Empire";

    // In the order the editor lists them.
    public static readonly IReadOnlyList<string> All = [Cube, Sphere, Capsule, Cone, Empire];

    private const int Segments = 32;      // around a round shape
    private const int SphereRings = 24;   // from pole to pole
    private const int CapRings = 12;      // from the pole to the edge of a capsule's end

    private static readonly Dictionary<string, GlbModel> Models = new();

    public static bool IsBuiltIn(string model) => model.StartsWith(Prefix, StringComparison.Ordinal);

    // The name to show for a model: "Cube" for a built-in one, otherwise its file name.
    public static string Label(string model) => IsBuiltIn(model) ? model[Prefix.Length..] : Path.GetFileName(model);

    public static GlbModel Get(string model)
    {
        lock (Models)
        {
            if (!Models.TryGetValue(model, out var built))
                Models[model] = built = Build(model);
            return built;
        }
    }

    private static GlbModel Build(string model) => model switch
    {
        Cube => BuildCube(),
        Sphere => Revolve("Sphere", SphereProfile()),
        Capsule => Revolve("Capsule", CapsuleProfile()),
        Cone => Revolve("Cone", ConeProfile()),
        Empire => LoadEmpire(),
        _ => throw new InvalidDataException($"There is no built-in model named {Label(model)}."),
    };

    private static GlbModel LoadEmpire()
    {
        using var stream = typeof(Primitives).Assembly.GetManifestResourceStream("Engine.Core.Assets.Empire_Crown.glb")
            ?? throw new InvalidDataException("The Empire crown is missing from Engine.Core.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return GlbLoader.Load(bytes.ToArray(), AppContext.BaseDirectory, "Empire");
    }

    private static MaterialData Material() => new([0.8f, 0.8f, 0.8f, 1f], 0.6f, 0f, 1.5f, 0.5f, [1, 1, 1], [0, 0, 0]);

    private static GlbModel Model(string name, List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, List<uint> indices)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        float[] Flat3(List<Vector3> values) => values.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray();
        var mesh = new MeshData(name, Flat3(positions), Flat3(normals), uvs.SelectMany(v => new[] { v.X, v.Y }).ToArray(), indices.ToArray(), Material());
        return new GlbModel { Name = name, Meshes = [mesh], BoundsMin = min, BoundsMax = max };
    }

    private static GlbModel BuildCube()
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<uint>();
        // Each face: its direction, and two edges that turn counterclockwise seen from outside.
        (Vector3 Normal, Vector3 U, Vector3 V)[] faces =
        [
            (Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY), (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ), (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY),
        ];
        foreach (var (normal, u, v) in faces)
        {
            uint first = (uint)positions.Count;
            foreach (var (su, sv) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                positions.Add((normal + u * su + v * sv) * 0.5f);
                normals.Add(normal);
                uvs.Add(new Vector2((su + 1f) / 2f, (1f - sv) / 2f));
            }
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }
        return Model("Cube", positions, normals, uvs, indices);
    }

    // One point of a shape's outline, turned around Y to make it: how far out and how high, and which way the surface faces there.
    private readonly record struct Outline(float Radius, float Y, float NormalOut, float NormalUp);

    // Turns an outline running from the bottom of a shape to the top into a mesh.
    private static GlbModel Revolve(string name, List<Outline> outline)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<uint>();
        for (int i = 0; i < outline.Count; i++)
            for (int j = 0; j <= Segments; j++)
            {
                float angle = MathF.Tau * j / Segments;
                float c = MathF.Cos(angle), s = MathF.Sin(angle);
                var point = outline[i];
                positions.Add(new Vector3(point.Radius * c, point.Y, point.Radius * s));
                normals.Add(Vector3.Normalize(new Vector3(point.NormalOut * c, point.NormalUp, point.NormalOut * s)));
                uvs.Add(new Vector2((float)j / Segments, 1f - (float)i / (outline.Count - 1)));
            }
        uint row = Segments + 1;
        for (uint i = 0; i < outline.Count - 1; i++)
            for (uint j = 0; j < Segments; j++)
            {
                uint a = i * row + j, b = a + 1, c = a + row, d = c + 1;
                indices.AddRange([a, c, b, b, c, d]);
            }
        return Model(name, positions, normals, uvs, indices);
    }

    private static List<Outline> SphereProfile()
    {
        var outline = new List<Outline>();
        for (int i = 0; i <= SphereRings; i++)
        {
            float angle = MathF.PI * ((float)i / SphereRings - 0.5f);
            outline.Add(new Outline(0.5f * MathF.Cos(angle), 0.5f * MathF.Sin(angle), MathF.Cos(angle), MathF.Sin(angle)));
        }
        return outline;
    }

    private static List<Outline> CapsuleProfile()
    {
        var outline = new List<Outline>();
        for (int i = 0; i <= CapRings; i++)   // the bottom end, from its pole up to the straight sides
        {
            float angle = MathF.PI / 2f * ((float)i / CapRings - 1f);
            outline.Add(new Outline(0.5f * MathF.Cos(angle), -0.5f + 0.5f * MathF.Sin(angle), MathF.Cos(angle), MathF.Sin(angle)));
        }
        for (int i = 0; i <= CapRings; i++)   // the top end
        {
            float angle = MathF.PI / 2f * i / CapRings;
            outline.Add(new Outline(0.5f * MathF.Cos(angle), 0.5f + 0.5f * MathF.Sin(angle), MathF.Cos(angle), MathF.Sin(angle)));
        }
        return outline;
    }

    // The rim appears twice, so the flat base and the slope each keep their own direction there and the edge stays sharp.
    private static List<Outline> ConeProfile() =>
    [
        new(0f, -0.5f, 0f, -1f),
        new(0.5f, -0.5f, 0f, -1f),
        new(0.5f, -0.5f, 1f, 0.5f),
        new(0f, 0.5f, 1f, 0.5f),
    ];
}
