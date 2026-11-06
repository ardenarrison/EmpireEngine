using System.Numerics;
using Engine.Core.Animation;
using Engine.Core.Assets;

namespace Editor.Modeling;

// One corner of a polygon. Key says which corners at a point share a normal: the same key is a smooth surface, a different one
// a hard edge.
public struct Corner
{
    public int Point;
    public Vector3 Normal;
    public Vector2 Uv;
    public Vector4 Color;
    public int Key;
}

// A flat-ish face with three or more corners, counter-clockwise from outside. Part says which of the model's materials it uses.
public sealed class Poly(Corner[] corners, int part)
{
    public Corner[] Corners { get; set; } = corners;
    public int Part { get; } = part;

    public Poly Copy() => new((Corner[])Corners.Clone(), Part);
}

// A model being edited: a polygon mesh of points and faces, and the arrays the renderer draws through Pose.
//
// Model files split a corner wherever its normal or texture coordinates differ: a cube has 24 vertices for its 8 corners, and they
// keep only triangles. So when a model is opened its vertices are joined into points, and pairs of triangles that make a flat
// four-sided face into quads. What the user selects and moves is a point, so the surface can't tear. Points are in model space,
// where the meshes sit in the model. The selection is a set of points: an edge or face is selected when all its points are.
//
// While the shape of the mesh hasn't changed, Model and the arrays are still those of the loaded file, in the same order, so the
// vertices can be written straight back into it. An operation that adds or removes anything builds new ones and Version goes up.
public sealed partial class ModelEdit
{
    private const int MaxUndoSteps = 64;

    private sealed record State(Vector3[] Points, Poly[] Polys, int[] Selection, ModelModifier[] Modifiers);
    private sealed record Step(int[]? Points, Vector3[]? Before, Vector3[]? After, State? StateBefore, State? StateAfter);

    private List<Vector3> _points = new();
    private List<Poly> _polys = new();
    private readonly List<MaterialData> _materials = new();
    private readonly List<string> _partNames = new();
    private int _nextKey = 1;

    // Where each point is in the arrays the renderer reads, what each vertex's key is, and how to get into a mesh's own space.
    private List<(int Mesh, int Vertex)>[] _members = [];
    private int[][] _vertexKeys = [];
    private Matrix4x4[] _toMesh = [];
    private bool[] _hasNormals = [];

    // What touches what, worked out again whenever the shape changes.
    private List<int>[] _polysOf = [];
    private Dictionary<(int, int), List<int>> _edgePolys = new();
    private List<int>[] _neighbors = [];
    private List<(int A, int B)> _edges = new();

    private readonly List<Step> _undo = new();
    private readonly Stack<Step> _redo = new();
    private int _savedAt;                     // how many steps were in _undo at the last save, or -1 when that state is gone
    private int _savedVersion;
    private int[]? _changing;
    private Vector3[]? _before;

    public GlbModel Model { get; private set; }
    public float[][] Positions { get; private set; }
    public float[][] Normals { get; private set; }
    public ModelPose Pose { get; private set; }
    public int Version { get; private set; }                     // goes up when the arrays are replaced
    public bool CanChangeShape { get; }                          // false for models with animations, which only a .glb in place can keep
    public HashSet<int> Selection { get; private set; } = new();

    // Modifiers run in this order on a copy of the mesh, for showing it, until they are applied. Saving applies them, because a model
    // file has nowhere to keep them.
    public List<ModelModifier> Modifiers { get; private set; } = new();
    public bool HasModifiers => Modifiers.Any(m => m.Enabled);
    public bool ModifiersStale { get; private set; }             // the mesh or a modifier changed since Evaluate last ran

    public IReadOnlyList<(int A, int B)> Edges => _edges;
    public IReadOnlyList<Poly> Polys => _polys;
    public int PointCount => _points.Count;
    public bool Dirty => _undo.Count != _savedAt || _changing is not null || HasModifiers;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // True when the file has different vertices from these arrays, so it has to be written anew instead of changed in place.
    public bool NeedsNewFile => Version != _savedVersion;

    // Why this model can't be edited, or null when it can.
    public static string? Problem(GlbModel model)
    {
        if (model.Meshes.Count == 0) return "This model has no meshes.";
        if (model.Meshes.Any(m => m.Skin >= 0)) return "Models with a skeleton can't be edited yet.";
        foreach (var shared in model.Meshes.Where(m => m.PositionAccessor >= 0).GroupBy(m => m.PositionAccessor))
            if (shared.Select(m => m.RestTransform).Distinct().Count() > 1)
                return "This model uses the same mesh in more than one place, which can't be edited yet.";
        return null;
    }

    public ModelEdit(GlbModel model)
    {
        Model = model;
        CanChangeShape = model.Animations.Count == 0;
        Positions = model.Meshes.Select(m => (float[])m.Positions.Clone()).ToArray();
        Normals = model.Meshes.Select(m => (float[])m.Normals.Clone()).ToArray();
        _hasNormals = Normals.Select(n => n.Any(v => v != 0f)).ToArray();
        _toMesh = model.Meshes.Select(m => Matrix4x4.Invert(m.RestTransform, out var inverse) ? inverse : Matrix4x4.Identity).ToArray();

        // Exporters write a corner's copies with the same numbers, so a very small tolerance finds them all.
        var extent = model.BoundsMax - model.BoundsMin;
        float cell = MathF.Max(MathF.Max(extent.X, extent.Y), MathF.Max(extent.Z, 1e-3f)) * 1e-5f;
        var cells = new Dictionary<(long, long, long), int>();
        var members = new List<List<(int, int)>>();
        var keys = new Dictionary<(int, int, int, int), int>();
        _vertexKeys = new int[model.Meshes.Count][];

        for (int m = 0; m < model.Meshes.Count; m++)
        {
            var mesh = model.Meshes[m];
            _materials.Add(mesh.Material);
            _partNames.Add(mesh.Name);
            int vertices = mesh.Positions.Length / 3;
            var pointOf = new int[vertices];
            var corners = new Corner[vertices];
            _vertexKeys[m] = new int[vertices];
            for (int v = 0; v < vertices; v++)
            {
                var at = Vector3.Transform(Read(mesh.Positions, v), mesh.RestTransform);
                var cellKey = ((long)MathF.Round(at.X / cell), (long)MathF.Round(at.Y / cell), (long)MathF.Round(at.Z / cell));
                if (!cells.TryGetValue(cellKey, out int point))
                {
                    cells[cellKey] = point = _points.Count;
                    _points.Add(at);
                    members.Add(new());
                }
                members[point].Add((m, v));
                pointOf[v] = point;

                var normal = Read(mesh.Normals, v);
                var normalKey = (point, (int)MathF.Round(normal.X * 1000f), (int)MathF.Round(normal.Y * 1000f), (int)MathF.Round(normal.Z * 1000f));
                if (!keys.TryGetValue(normalKey, out int key))
                    keys[normalKey] = key = _nextKey++;
                _vertexKeys[m][v] = key;
                corners[v] = MakeCorner(point, Vector3.Normalize(Vector3.TransformNormal(normal, mesh.RestTransform) + new Vector3(0f, 0f, 1e-12f)), mesh, v, key);
            }
            AddTriangles(mesh.Indices, corners, m);
        }
        _members = members.ToArray();
        Connect();
        Pose = MakePose();
    }

    public Vector3 Point(int index) => _points[index];

    public Vector3 Center(IEnumerable<int> points)
    {
        var sum = Vector3.Zero;
        int count = 0;
        foreach (int point in points)
        {
            sum += _points[point];
            count++;
        }
        return count == 0 ? Vector3.Zero : sum / count;
    }

    // The direction a face looks in. Its length is twice its area.
    public Vector3 FaceNormal(Poly poly) => Normal(poly, _points);

    public static Vector3 Normal(Poly poly, IReadOnlyList<Vector3> points)
    {
        var sum = Vector3.Zero;
        var corners = poly.Corners;
        for (int i = 0; i < corners.Length; i++)
        {
            var a = points[corners[i].Point];
            var b = points[corners[(i + 1) % corners.Length].Point];
            sum += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        return sum;
    }

    public Vector3 FaceCenter(Poly poly) => Center(poly.Corners.Select(c => c.Point));

    public bool IsSelected(Poly poly) => poly.Corners.All(c => Selection.Contains(c.Point));

    public IReadOnlyList<int> PolysOf(int point) => _polysOf[point];

    // Changes to points are made between BeginChange and EndChange, which makes them one undo step.
    public void BeginChange(IEnumerable<int> points)
    {
        _changing = points.ToArray();
        _before = _changing.Select(p => _points[p]).ToArray();
    }

    public void SetPoint(int index, Vector3 position)
    {
        _points[index] = position;
        ModifiersStale = true;
        foreach (var (mesh, vertex) in _members[index])
            Write(Positions[mesh], vertex, Vector3.Transform(position, _toMesh[mesh]));
    }

    public void CancelChange()
    {
        if (_changing is not { } points) return;
        for (int i = 0; i < points.Length; i++)
            SetPoint(points[i], _before![i]);
        UpdateNormals(points);
        _changing = null;
        _before = null;
    }

    public void EndChange()
    {
        if (_changing is not { } points) return;
        var before = _before!;
        var after = points.Select(p => _points[p]).ToArray();
        _changing = null;
        _before = null;
        if (!before.SequenceEqual(after))
            Push(new Step(points, before, after, null, null));
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Push(step);
        if (step.StateBefore is { } state) Restore(state);
        else Apply(step.Points!, step.Before!);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var step = _redo.Pop();
        _undo.Add(step);
        if (step.StateAfter is { } state) Restore(state);
        else Apply(step.Points!, step.After!);
    }

    public void AddModifier(ModelModifier modifier)
    {
        Modifiers.Add(modifier);
        ModifiersChanged();
    }

    public void RemoveModifier(ModelModifier modifier)
    {
        Modifiers.Remove(modifier);
        ModifiersChanged();
    }

    // Moves a modifier earlier (-1) or later (1) in the stack: the same modifiers in a different order give a different result.
    public void MoveModifier(ModelModifier modifier, int by)
    {
        int at = Modifiers.IndexOf(modifier);
        int to = Math.Clamp(at + by, 0, Modifiers.Count - 1);
        Modifiers.RemoveAt(at);
        Modifiers.Insert(to, modifier);
        ModifiersChanged();
    }

    public void ModifiersChanged() => ModifiersStale = true;

    // The mesh as the modifiers make it, to show. The mesh being edited stays as it is.
    public GlbModel Evaluate()
    {
        ModifiersStale = false;
        var work = RunModifiers(Modifiers.Where(m => m.Enabled));
        work.ComputeNormals();
        var (meshes, _, _) = BuildMeshes(work.Points, work.Polys);
        var (min, max) = work.Bounds();
        return new GlbModel { Name = Model.Name, Meshes = meshes, BoundsMin = min, BoundsMax = max };
    }

    // Makes this modifier's result the mesh, and takes it off the stack, as one undo step. Modifiers before it are not included, so one
    // that isn't first may give a different result from what was shown.
    public void ApplyModifier(ModelModifier modifier)
    {
        ChangeShape(() =>
        {
            var work = RunModifiers([modifier]);
            _points = work.Points;
            _polys = work.Polys;
            Modifiers.Remove(modifier);
            Selection.Clear();
        }, allNormals: true);
    }

    // Makes the whole stack's result the mesh, as one undo step.
    public void ApplyModifiers()
    {
        ChangeShape(() =>
        {
            var work = RunModifiers(Modifiers.Where(m => m.Enabled));
            _points = work.Points;
            _polys = work.Polys;
            Modifiers.Clear();
            Selection.Clear();
        }, allNormals: true);
    }

    private MeshWork RunModifiers(IEnumerable<ModelModifier> modifiers)
    {
        var work = new MeshWork(_points.ToList(), _polys.Select(p => p.Copy()).ToList());
        foreach (var modifier in modifiers)
            work = modifier.Run(work);
        return new MeshWork(work.Points.ToList(), work.Polys.Select(p => p.Copy()).ToList());
    }

    public void MarkSaved()
    {
        _savedAt = _undo.Count;
        _savedVersion = Version;
    }

    // Points this edit at the model that was just saved as a new file: the same meshes in the same order, now with a place in a .glb.
    public void Rebind(GlbModel saved)
    {
        Model = saved;
        Pose = MakePose();
    }

    // Works out the normals around these points again, leaving the rest of the model's as they came.
    public void UpdateNormals(IReadOnlyCollection<int> moved)
    {
        var polys = new HashSet<int>();
        foreach (int point in moved)
            polys.UnionWith(_polysOf[point]);

        var groups = new HashSet<(int Point, int Key)>();
        foreach (int index in polys)
            foreach (var corner in _polys[index].Corners)
                groups.Add((corner.Point, corner.Key));

        var around = new HashSet<int>();
        foreach (var (point, _) in groups)
            around.UnionWith(_polysOf[point]);

        var sums = new Dictionary<(int, int), Vector3>();
        foreach (int index in around)
        {
            var poly = _polys[index];
            var face = FaceNormal(poly);
            foreach (var corner in poly.Corners)
                if (groups.Contains((corner.Point, corner.Key)))
                    sums[(corner.Point, corner.Key)] = sums.GetValueOrDefault((corner.Point, corner.Key)) + face;
        }

        foreach (int index in around)
        {
            var corners = _polys[index].Corners;
            for (int i = 0; i < corners.Length; i++)
                if (sums.TryGetValue((corners[i].Point, corners[i].Key), out var sum) && sum.LengthSquared() > 1e-20f)
                    corners[i].Normal = Vector3.Normalize(sum);
        }

        foreach (var ((point, key), sum) in sums)
        {
            if (sum.LengthSquared() <= 1e-20f) continue;
            var normal = Vector3.Normalize(sum);
            foreach (var (mesh, vertex) in _members[point])
                if (_hasNormals[mesh] && _vertexKeys[mesh][vertex] == key)
                {
                    var local = Vector3.TransformNormal(normal, _toMesh[mesh]);
                    Write(Normals[mesh], vertex, local.LengthSquared() > 1e-20f ? Vector3.Normalize(local) : normal);
                }
        }
    }

    // Runs an operation that changes the shape of the mesh, as one undo step. Normals are worked out again around the selection the
    // operation leaves, or everywhere if it said so.
    private void ChangeShape(Action operation, bool allNormals = false)
    {
        var before = Capture();
        operation();
        Compact();
        Connect();
        Rebuild();
        UpdateNormals(allNormals ? Enumerable.Range(0, _points.Count).ToList() : Selection);
        Push(new Step(null, null, null, before, Capture()));
    }

    private void Push(Step step)
    {
        if (_savedAt > _undo.Count) _savedAt = -1;   // the saved state was undone and is now out of reach
        _undo.Add(step);
        _redo.Clear();
        if (_undo.Count > MaxUndoSteps)
        {
            _undo.RemoveAt(0);
            if (_savedAt >= 0) _savedAt--;
        }
    }

    private State Capture() => new(_points.ToArray(), _polys.Select(p => p.Copy()).ToArray(), Selection.ToArray(), Modifiers.ToArray());

    private void Restore(State state)
    {
        _points = state.Points.ToList();
        _polys = state.Polys.Select(p => p.Copy()).ToList();
        Selection = state.Selection.ToHashSet();
        Modifiers = state.Modifiers.ToList();
        Connect();
        Rebuild();
    }

    private void Apply(int[] points, Vector3[] positions)
    {
        for (int i = 0; i < points.Length; i++)
            SetPoint(points[i], positions[i]);
        UpdateNormals(points);
    }

    // Removes points that no face uses any more, and faces that have folded up.
    private void Compact()
    {
        _polys.RemoveAll(p => p.Corners.Select(c => c.Point).Distinct().Count() < 3);
        var used = new bool[_points.Count];
        foreach (var poly in _polys)
            foreach (var corner in poly.Corners)
                used[corner.Point] = true;

        var moved = new int[_points.Count];
        var kept = new List<Vector3>();
        for (int i = 0; i < _points.Count; i++)
        {
            moved[i] = used[i] ? kept.Count : -1;
            if (used[i]) kept.Add(_points[i]);
        }
        _points = kept;
        foreach (var poly in _polys)
            for (int i = 0; i < poly.Corners.Length; i++)
                poly.Corners[i].Point = moved[poly.Corners[i].Point];
        Selection = Selection.Where(p => p < moved.Length && moved[p] >= 0).Select(p => moved[p]).ToHashSet();
    }

    private void Connect()
    {
        _polysOf = new List<int>[_points.Count];
        _neighbors = new List<int>[_points.Count];
        for (int i = 0; i < _points.Count; i++)
        {
            _polysOf[i] = new();
            _neighbors[i] = new();
        }
        _edgePolys = new();
        _edges = new();
        for (int p = 0; p < _polys.Count; p++)
        {
            var corners = _polys[p].Corners;
            for (int i = 0; i < corners.Length; i++)
            {
                int a = corners[i].Point, b = corners[(i + 1) % corners.Length].Point;
                _polysOf[a].Add(p);
                if (a == b) continue;
                var edge = EdgeKey(a, b);
                if (!_edgePolys.TryGetValue(edge, out var list))
                {
                    _edgePolys[edge] = list = new();
                    _edges.Add(edge);
                    _neighbors[a].Add(b);
                    _neighbors[b].Add(a);
                }
                list.Add(p);
            }
        }
    }

    // Makes the arrays the renderer reads, and the model that goes with them, from the faces.
    private void Rebuild()
    {
        var (meshes, members, keys) = BuildMeshes(_points, _polys);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var point in _points)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
        if (meshes.Count == 0) min = max = Vector3.Zero;

        Model = new GlbModel { Name = Model.Name, Meshes = meshes, BoundsMin = min, BoundsMax = max };
        Positions = meshes.Select(m => m.Positions).ToArray();
        Normals = meshes.Select(m => m.Normals).ToArray();
        _members = members;
        _vertexKeys = keys.ToArray();
        _toMesh = meshes.Select(_ => Matrix4x4.Identity).ToArray();
        _hasNormals = meshes.Select(_ => true).ToArray();
        Pose = MakePose();
        ModifiersStale = true;
        Version++;
    }

    // One mesh for each material that has faces, where each point went in them, and each vertex's key.
    private (List<MeshData> Meshes, List<(int, int)>[] Members, List<int[]> Keys) BuildMeshes(IReadOnlyList<Vector3> points, IReadOnlyList<Poly> polys)
    {
        var builders = _materials.Select(_ => new PartBuilder()).ToArray();
        var members = new List<(int, int)>[points.Count];
        for (int i = 0; i < members.Length; i++) members[i] = new();

        foreach (var poly in polys)
        {
            var part = builders[poly.Part];
            var corners = poly.Corners;
            var vertices = new uint[corners.Length];
            for (int i = 0; i < corners.Length; i++)
                vertices[i] = part.Vertex(corners[i], points[corners[i].Point]);
            foreach (var (a, b, c) in Triangulate(poly, points))
            {
                part.Indices.Add(vertices[a]);
                part.Indices.Add(vertices[b]);
                part.Indices.Add(vertices[c]);
            }
        }

        var meshes = new List<MeshData>();
        var keys = new List<int[]>();
        for (int m = 0; m < builders.Length; m++)
        {
            var part = builders[m];
            if (part.Indices.Count == 0) continue;
            for (int v = 0; v < part.Points.Count; v++)
                members[part.Points[v]].Add((meshes.Count, v));
            meshes.Add(part.ToMesh(_partNames[m], _materials[m]));
            keys.Add(part.Keys.ToArray());
        }
        return (meshes, members, keys);
    }

    // The triangles of a face, as positions in its corners. Faces with more than three corners may not be convex, so ears are cut off.
    public static List<(int, int, int)> Triangulate(Poly poly, IReadOnlyList<Vector3> points)
    {
        var corners = poly.Corners;
        var result = new List<(int, int, int)>(corners.Length - 2);
        if (corners.Length == 3)
        {
            result.Add((0, 1, 2));
            return result;
        }

        var normal = Normal(poly, points);
        var remaining = Enumerable.Range(0, corners.Length).ToList();
        Vector3 At(int i) => points[corners[i].Point];
        bool Ear(int previous, int current, int next)
        {
            var a = At(previous);
            var b = At(current);
            var c = At(next);
            if (Vector3.Dot(Vector3.Cross(b - a, c - b), normal) <= 0f) return false;
            foreach (int other in remaining)
            {
                if (other == previous || other == current || other == next) continue;
                var p = At(other);
                if (Vector3.Dot(Vector3.Cross(b - a, p - a), normal) >= 0f &&
                    Vector3.Dot(Vector3.Cross(c - b, p - b), normal) >= 0f &&
                    Vector3.Dot(Vector3.Cross(a - c, p - c), normal) >= 0f)
                    return false;
            }
            return true;
        }

        while (remaining.Count > 3)
        {
            int cut = -1;
            for (int i = 0; i < remaining.Count && cut < 0; i++)
                if (Ear(remaining[(i + remaining.Count - 1) % remaining.Count], remaining[i], remaining[(i + 1) % remaining.Count]))
                    cut = i;
            if (cut < 0) cut = 0;   // a face that crosses itself has no ears: this draws it as well as it can be
            result.Add((remaining[(cut + remaining.Count - 1) % remaining.Count], remaining[cut], remaining[(cut + 1) % remaining.Count]));
            remaining.RemoveAt(cut);
        }
        result.Add((remaining[0], remaining[1], remaining[2]));
        return result;
    }

    // Adds a mesh's triangles as faces, joining pairs that make a flat four-sided face: the pair's shared edge is the longest edge of
    // both, which is how a quad is split in two.
    private void AddTriangles(uint[] indices, Corner[] corners, int part)
    {
        int count = indices.Length / 3;
        var edgeOwners = new Dictionary<(int, int), List<int>>();
        var longest = new (int A, int B)[count];
        var normals = new Vector3[count];
        var degenerate = new bool[count];
        for (int t = 0; t < count; t++)
        {
            int a = corners[indices[t * 3]].Point, b = corners[indices[t * 3 + 1]].Point, c = corners[indices[t * 3 + 2]].Point;
            if (a == b || b == c || c == a)
            {
                degenerate[t] = true;   // the pole of a sphere has these: they have no area and aren't faces
                continue;
            }
            var pa = _points[a];
            var pb = _points[b];
            var pc = _points[c];
            normals[t] = Vector3.Cross(pb - pa, pc - pa);
            float ab = Vector3.DistanceSquared(pa, pb), bc = Vector3.DistanceSquared(pb, pc), ca = Vector3.DistanceSquared(pc, pa);
            longest[t] = ab >= bc && ab >= ca ? EdgeKey(a, b) : bc >= ca ? EdgeKey(b, c) : EdgeKey(c, a);
            foreach (var edge in new[] { EdgeKey(a, b), EdgeKey(b, c), EdgeKey(c, a) })
            {
                if (!edgeOwners.TryGetValue(edge, out var owners))
                    edgeOwners[edge] = owners = new();
                owners.Add(t);
            }
        }

        var joined = new bool[count];
        for (int t = 0; t < count; t++)
        {
            if (joined[t] || degenerate[t]) continue;
            var tri = TriangleCorners(indices, corners, t);
            int other = edgeOwners[longest[t]] is { Count: 2 } pair ? pair[0] == t ? pair[1] : pair[0] : -1;
            bool quad = other >= 0 && !joined[other] && !degenerate[other] && longest[other] == longest[t] && Flat(normals[t], normals[other]);
            if (quad)
            {
                // Turn the triangle so that the shared edge is its last two corners; the other triangle's third corner goes between them.
                while (EdgeKey(tri[1].Point, tri[2].Point) != longest[t])
                    tri = [tri[1], tri[2], tri[0]];
                var far = TriangleCorners(indices, corners, other).First(c => c.Point != tri[1].Point && c.Point != tri[2].Point);
                _polys.Add(new Poly([tri[0], tri[1], far, tri[2]], part));
                joined[other] = true;
            }
            else
                _polys.Add(new Poly(tri, part));
            joined[t] = true;
        }
    }

    private static Corner[] TriangleCorners(uint[] indices, Corner[] corners, int triangle) =>
        [corners[indices[triangle * 3]], corners[indices[triangle * 3 + 1]], corners[indices[triangle * 3 + 2]]];

    private static bool Flat(Vector3 a, Vector3 b)
    {
        float lengths = a.Length() * b.Length();
        return lengths > 1e-20f && Vector3.Dot(a, b) / lengths > 0.97f;
    }

    private static Corner MakeCorner(int point, Vector3 normal, MeshData mesh, int vertex, int key) => new()
    {
        Point = point,
        Normal = normal,
        Uv = vertex * 2 + 1 < mesh.TexCoords.Length ? new Vector2(mesh.TexCoords[vertex * 2], mesh.TexCoords[vertex * 2 + 1]) : Vector2.Zero,
        Color = mesh.Colors is { } colors && vertex * 4 + 3 < colors.Length
            ? new Vector4(colors[vertex * 4], colors[vertex * 4 + 1], colors[vertex * 4 + 2], colors[vertex * 4 + 3])
            : Vector4.One,
        Key = key,
    };

    private ModelPose MakePose() => new()
    {
        Model = Model,
        MeshTransforms = Model.Meshes.Select(m => m.RestTransform).ToArray(),
        Positions = Positions,
        Normals = Normals,
    };

    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

    private static Vector3 Read(float[] values, int vertex) => new(values[vertex * 3], values[vertex * 3 + 1], values[vertex * 3 + 2]);

    private static void Write(float[] values, int vertex, Vector3 value)
    {
        values[vertex * 3] = value.X;
        values[vertex * 3 + 1] = value.Y;
        values[vertex * 3 + 2] = value.Z;
    }

    // One part's vertices while the arrays are being made: corners that agree on everything share a vertex.
    private sealed class PartBuilder
    {
        private readonly Dictionary<(int, int, Vector2, Vector4), uint> _vertices = new();
        private readonly List<float> _positions = new(), _normals = new(), _uvs = new(), _colors = new();
        private bool _hasColors;

        public List<uint> Indices { get; } = new();
        public List<int> Points { get; } = new();
        public List<int> Keys { get; } = new();

        public uint Vertex(Corner corner, Vector3 position)
        {
            var key = (corner.Point, corner.Key, corner.Uv, corner.Color);
            if (_vertices.TryGetValue(key, out uint known)) return known;
            uint vertex = (uint)Points.Count;
            _vertices[key] = vertex;
            Points.Add(corner.Point);
            Keys.Add(corner.Key);
            _positions.AddRange([position.X, position.Y, position.Z]);
            _normals.AddRange([corner.Normal.X, corner.Normal.Y, corner.Normal.Z]);
            _uvs.AddRange([corner.Uv.X, corner.Uv.Y]);
            _colors.AddRange([corner.Color.X, corner.Color.Y, corner.Color.Z, corner.Color.W]);
            _hasColors |= corner.Color != Vector4.One;
            return vertex;
        }

        public MeshData ToMesh(string name, MaterialData material) =>
            new(name, _positions.ToArray(), _normals.ToArray(), _uvs.ToArray(), Indices.ToArray(), material)
            {
                Colors = _hasColors ? _colors.ToArray() : null,
            };
    }
}
