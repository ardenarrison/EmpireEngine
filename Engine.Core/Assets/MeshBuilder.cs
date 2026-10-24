using System.Numerics;

namespace Engine.Core.Assets;

// One polygon corner: indices into the builder's position, normal and UV arrays. -1 means the file has none.
internal readonly record struct Corner(int Position, int Normal, int Uv);

// Turns polygons into an indexed MeshData for the OBJ and FBX loaders. Those formats index positions,
// normals and UVs separately, so corners with the same three indices share one vertex.
// Polygons are fan-triangulated. Vertices without a normal get a smooth one from the faces around them.
internal sealed class MeshBuilder(Vector3[] positions, Vector3[] normals, Vector2[] uvs, bool flipWinding)
{
    private readonly List<float> _positions = new();
    private readonly List<float> _normals = new();
    private readonly List<float> _uvs = new();
    private readonly List<uint> _indices = new();
    private readonly List<int> _sourcePosition = new();   // per output vertex: its index in positions
    private readonly Dictionary<Corner, uint> _vertices = new();
    private bool _missingNormals;

    public bool IsEmpty => _indices.Count == 0;

    public void AddPolygon(IReadOnlyList<Corner> corners)
    {
        if (corners.Count < 3) return;   // points and edges are not drawn
        for (int i = 1; i + 1 < corners.Count; i++)
        {
            uint a = Vertex(corners[0]), b = Vertex(corners[i]), c = Vertex(corners[i + 1]);
            if (flipWinding) (b, c) = (c, b);
            _indices.Add(a);
            _indices.Add(b);
            _indices.Add(c);
        }
    }

    private uint Vertex(Corner corner)
    {
        if ((uint)corner.Position >= positions.Length)
            throw new InvalidDataException($"Polygon refers to vertex {corner.Position}, but the mesh has {positions.Length}.");
        if ((uint)corner.Normal >= normals.Length) corner = corner with { Normal = -1 };
        if ((uint)corner.Uv >= uvs.Length) corner = corner with { Uv = -1 };

        if (_vertices.TryGetValue(corner, out uint index))
            return index;

        index = (uint)_sourcePosition.Count;
        _vertices[corner] = index;
        _sourcePosition.Add(corner.Position);

        var p = positions[corner.Position];
        _positions.AddRange([p.X, p.Y, p.Z]);
        var n = corner.Normal >= 0 ? normals[corner.Normal] : Vector3.Zero;
        _normals.AddRange([n.X, n.Y, n.Z]);
        var uv = corner.Uv >= 0 ? uvs[corner.Uv] : Vector2.Zero;
        _uvs.AddRange([uv.X, uv.Y]);
        _missingNormals |= corner.Normal < 0;
        return index;
    }

    public MeshData Build(string name, MaterialData material)
    {
        var normalArray = _normals.ToArray();
        if (_missingNormals)
            FillSmoothNormals(normalArray);
        return new MeshData(name, _positions.ToArray(), normalArray, _uvs.ToArray(), _indices.ToArray(), material);
    }

    // Area-weighted face normals summed per source position, so UV seams don't show as lighting seams.
    private void FillSmoothNormals(float[] normalArray)
    {
        var sums = new Dictionary<int, Vector3>();
        for (int i = 0; i + 2 < _indices.Count; i += 3)
        {
            int a = (int)_indices[i], b = (int)_indices[i + 1], c = (int)_indices[i + 2];
            var face = Vector3.Cross(Position(b) - Position(a), Position(c) - Position(a));
            foreach (int v in (ReadOnlySpan<int>)[a, b, c])
                sums[_sourcePosition[v]] = sums.GetValueOrDefault(_sourcePosition[v]) + face;
        }

        for (int v = 0; v < _sourcePosition.Count; v++)
        {
            if (normalArray[v * 3] != 0 || normalArray[v * 3 + 1] != 0 || normalArray[v * 3 + 2] != 0) continue;
            var n = sums.GetValueOrDefault(_sourcePosition[v]);
            n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
            normalArray[v * 3] = n.X;
            normalArray[v * 3 + 1] = n.Y;
            normalArray[v * 3 + 2] = n.Z;
        }
    }

    private Vector3 Position(int v) => new(_positions[v * 3], _positions[v * 3 + 1], _positions[v * 3 + 2]);

    // The material used when a file gives none, matching GlbLoader's: white, roughness 0.5, not metallic.
    public static MaterialData DefaultMaterial(float[]? baseColor = null, float roughness = 0.5f,
        float metallic = 0f, float[]? emission = null) =>
        new(baseColor ?? [1, 1, 1, 1], roughness, metallic, 1.5f, 1f, [1, 1, 1], emission ?? [0, 0, 0]);

    // Wraps finished meshes into a model, with bounds measured from their vertices.
    public static GlbModel Model(string path, List<MeshData> meshes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in meshes)
            for (int i = 0; i + 2 < mesh.Positions.Length; i += 3)
            {
                var p = new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        if (meshes.Count == 0) min = max = Vector3.Zero;

        return new GlbModel { Name = Path.GetFileNameWithoutExtension(path), Meshes = meshes, BoundsMin = min, BoundsMax = max };
    }
}
