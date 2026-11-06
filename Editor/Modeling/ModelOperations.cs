using System.Numerics;
using Engine.Core.Assets;

namespace Editor.Modeling;

public enum ModelPrimitive { Plane, Cube, Circle, Sphere, Cylinder, Cone, Torus, Grid, Capsule, Crown }

public enum DeleteKind { Vertices, Edges, Faces }

// What can be done to the mesh. Each operation is one undo step, and works on the selection. The ones that return a string return
// why nothing happened, or null when it worked.
public sealed partial class ModelEdit
{
    private const int RoundSegments = 32;

    // ---- selecting -----------------------------------------------------------------------------

    public void SelectAll()
    {
        Selection = Enumerable.Range(0, _points.Count).ToHashSet();
    }

    public void SelectNone() => Selection.Clear();

    public void SelectInverse()
    {
        Selection = Enumerable.Range(0, _points.Count).Where(p => !Selection.Contains(p)).ToHashSet();
    }

    // Everything joined to the selection.
    public void SelectLinked()
    {
        var waiting = new Stack<int>(Selection);
        while (waiting.TryPop(out int point))
            foreach (int next in _neighbors[point])
                if (Selection.Add(next))
                    waiting.Push(next);
    }

    // Everything joined to this point: one loose piece of the model. Adds it to the selection, or with remove takes it out.
    public void SelectPiece(int start, bool remove = false)
    {
        var piece = new HashSet<int> { start };
        var waiting = new Stack<int>(piece);
        while (waiting.TryPop(out int point))
            foreach (int next in _neighbors[point])
                if (piece.Add(next))
                    waiting.Push(next);
        if (remove) Selection.ExceptWith(piece);
        else Selection.UnionWith(piece);
    }

    public void SelectMore()
    {
        foreach (int point in Selection.ToList())
            Selection.UnionWith(_neighbors[point]);
    }

    public void SelectLess()
    {
        Selection = Selection.Where(p => _neighbors[p].All(Selection.Contains)).ToHashSet();
    }

    // The line of edges that runs straight on from this one, through corners where four edges meet.
    public IReadOnlyList<int> EdgeLoop(int a, int b)
    {
        var loop = new HashSet<int> { a, b };
        void Walk(int from, int at)
        {
            while (true)
            {
                if (_neighbors[at].Count != 4) return;
                var touching = _edgePolys[EdgeKey(from, at)];
                int next = _neighbors[at].Where(n => n != from && !_edgePolys[EdgeKey(at, n)].Any(touching.Contains)).DefaultIfEmpty(-1).First();
                if (next < 0 || !loop.Add(next)) return;
                (from, at) = (at, next);
            }
        }
        Walk(a, b);
        Walk(b, a);
        return loop.ToList();
    }

    // The edges across from this one, through a strip of four-sided faces.
    public IReadOnlyList<(int A, int B)> EdgeRing(int a, int b)
    {
        var ring = new List<(int, int)> { EdgeKey(a, b) };
        var seen = new HashSet<(int, int)>(ring);
        var waiting = new Stack<(int, int)>(ring);
        while (waiting.TryPop(out var edge))
            foreach (int index in _edgePolys[edge])
            {
                var corners = _polys[index].Corners;
                if (corners.Length != 4) continue;
                int at = Array.FindIndex(corners, c => c.Point == edge.Item1);
                int other = corners[(at + 1) % 4].Point == edge.Item2 ? (at + 2) % 4 : (at + 3) % 4;
                var across = EdgeKey(corners[other].Point, corners[(other + 1) % 4].Point);
                if (seen.Add(across))
                {
                    ring.Add(across);
                    waiting.Push(across);
                }
            }
        return ring;
    }

    // ---- adding --------------------------------------------------------------------------------

    public void AddPrimitive(ModelPrimitive kind, Vector3 at)
    {
        ChangeShape(() =>
        {
            int firstPoint = _points.Count;
            switch (kind)
            {
                case ModelPrimitive.Cube: Import(Primitives.Cube, at); break;
                case ModelPrimitive.Sphere: Import(Primitives.Sphere, at); break;
                case ModelPrimitive.Cone: Import(Primitives.Cone, at); break;
                case ModelPrimitive.Capsule: Import(Primitives.Capsule, at); break;
                case ModelPrimitive.Crown: Import(Primitives.Empire, at); break;
                case ModelPrimitive.Plane: Grid(1, at); break;
                case ModelPrimitive.Grid: Grid(10, at); break;
                case ModelPrimitive.Circle: Circle(at); break;
                case ModelPrimitive.Cylinder: Cylinder(at); break;
                default: Torus(at); break;
            }
            Selection = Enumerable.Range(firstPoint, _points.Count - firstPoint).ToHashSet();
        });
    }

    private void Import(string shape, Vector3 at)
    {
        foreach (var mesh in Primitives.Get(shape).Meshes)
        {
            int vertices = mesh.Positions.Length / 3;
            var corners = new Corner[vertices];
            var points = new Dictionary<(int, int, int), int>();
            var keys = new Dictionary<(int, Vector3), int>();
            for (int v = 0; v < vertices; v++)
            {
                var position = Read(mesh.Positions, v);
                var cell = ((int)MathF.Round(position.X * 1e5f), (int)MathF.Round(position.Y * 1e5f), (int)MathF.Round(position.Z * 1e5f));
                if (!points.TryGetValue(cell, out int point))
                {
                    points[cell] = point = _points.Count;
                    _points.Add(position + at);
                }
                var normal = Read(mesh.Normals, v);
                if (!keys.TryGetValue((point, normal), out int key))
                    keys[(point, normal)] = key = _nextKey++;
                corners[v] = MakeCorner(point, normal, mesh, v, key);
            }
            AddTriangles(mesh.Indices, corners, 0);
        }
    }

    // Adds faces given as lists of new points. Flat shading when smooth is false.
    private void AddFaces(IReadOnlyList<Vector3> points, IEnumerable<int[]> faces, bool smooth)
    {
        int first = _points.Count;
        _points.AddRange(points);
        int shared = _nextKey++;
        var low = points.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var size = points.Aggregate(new Vector3(float.MinValue), Vector3.Max) - low;
        foreach (var face in faces)
        {
            int key = smooth ? shared : _nextKey++;
            var corners = face.Select(i => new Corner
            {
                Point = first + i,
                Normal = Vector3.UnitY,
                Uv = new Vector2((points[i].X - low.X) / MathF.Max(size.X, 1e-6f), (points[i].Z - low.Z) / MathF.Max(size.Z, 1e-6f)),
                Color = Vector4.One,
                Key = key,
            }).ToArray();
            _polys.Add(new Poly(corners, 0));
        }
    }

    private void Grid(int cells, Vector3 at)
    {
        var points = new List<Vector3>();
        for (int z = 0; z <= cells; z++)
            for (int x = 0; x <= cells; x++)
                points.Add(at + new Vector3(x / (float)cells - 0.5f, 0f, z / (float)cells - 0.5f));
        var faces = new List<int[]>();
        int row = cells + 1;
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
                faces.Add([z * row + x, (z + 1) * row + x, (z + 1) * row + x + 1, z * row + x + 1]);
        AddFaces(points, faces, smooth: false);
    }

    private static Vector3 OnCircle(int index, float radius, float y) =>
        new(MathF.Cos(index * MathF.Tau / RoundSegments) * radius, y, MathF.Sin(index * MathF.Tau / RoundSegments) * radius);

    private void Circle(Vector3 at)
    {
        var points = Enumerable.Range(0, RoundSegments).Select(i => at + OnCircle(i, 0.5f, 0f)).ToList();
        AddFaces(points, [Enumerable.Range(0, RoundSegments).Reverse().ToArray()], smooth: false);
    }

    private void Cylinder(Vector3 at)
    {
        var points = Enumerable.Range(0, RoundSegments).Select(i => at + OnCircle(i, 0.5f, 0.5f))
            .Concat(Enumerable.Range(0, RoundSegments).Select(i => at + OnCircle(i, 0.5f, -0.5f))).ToList();
        int first = _points.Count;
        var sides = Enumerable.Range(0, RoundSegments).Select(i =>
        {
            int next = (i + 1) % RoundSegments;
            return new[] { i, next, next + RoundSegments, i + RoundSegments };
        }).ToList();
        AddFaces(points, sides, smooth: true);

        // The caps share the side's points, with keys of their own so that the rim stays a hard edge.
        foreach (var (start, order) in new[] { (0, Enumerable.Range(0, RoundSegments).Reverse()), (RoundSegments, Enumerable.Range(0, RoundSegments)) })
        {
            int key = _nextKey++;
            var corners = order.Select(i => new Corner
            {
                Point = first + start + i,
                Normal = Vector3.UnitY,
                Uv = new Vector2(MathF.Cos(i * MathF.Tau / RoundSegments), MathF.Sin(i * MathF.Tau / RoundSegments)) * 0.5f + new Vector2(0.5f),
                Color = Vector4.One,
                Key = key,
            }).ToArray();
            _polys.Add(new Poly(corners, 0));
        }
    }

    private void Torus(Vector3 at)
    {
        const int around = RoundSegments, tube = 12;
        var points = new List<Vector3>();
        for (int i = 0; i < around; i++)
            for (int j = 0; j < tube; j++)
            {
                float angle = j * MathF.Tau / tube;
                float radius = 0.375f + MathF.Cos(angle) * 0.125f;
                points.Add(at + OnCircle(i, radius, MathF.Sin(angle) * 0.125f));
            }
        var faces = new List<int[]>();
        for (int i = 0; i < around; i++)
            for (int j = 0; j < tube; j++)
            {
                int nextI = (i + 1) % around, nextJ = (j + 1) % tube;
                faces.Add([i * tube + j, i * tube + nextJ, nextI * tube + nextJ, nextI * tube + j]);
            }
        AddFaces(points, faces, smooth: true);
    }

    // ---- changing the shape --------------------------------------------------------------------

    // Pulls the selected faces out, or the selected edges if no whole face is selected. The new points are left selected, and in
    // the same place: normal is the way they should go, for the view to move them. Returns null if there is nothing to extrude.
    public Vector3? Extrude()
    {
        var faces = SelectedPolys();
        var edges = _edges.Where(e => Selection.Contains(e.A) && Selection.Contains(e.B)).ToList();
        if (faces.Count == 0 && edges.Count == 0) return null;

        var direction = Vector3.Zero;
        foreach (var poly in faces) direction += FaceNormal(poly);
        if (direction.LengthSquared() < 1e-12f)
            foreach (var edge in edges)
                foreach (int index in _edgePolys[edge])
                    direction += FaceNormal(_polys[index]);

        ChangeShape(() =>
        {
            var copies = new Dictionary<int, int>();
            int Copy(int point)
            {
                if (copies.TryGetValue(point, out int copy)) return copy;
                copies[point] = copy = _points.Count;
                _points.Add(_points[point]);
                return copy;
            }

            if (faces.Count > 0)
            {
                var chosen = faces.ToHashSet();
                var sides = new List<(Corner A, Corner B, int Part)>();
                foreach (var poly in faces)
                {
                    var corners = poly.Corners;
                    for (int i = 0; i < corners.Length; i++)
                    {
                        var a = corners[i];
                        var b = corners[(i + 1) % corners.Length];
                        if (_edgePolys[EdgeKey(a.Point, b.Point)].Count(index => chosen.Contains(_polys[index])) == 1)
                            sides.Add((a, b, poly.Part));
                    }
                }
                foreach (var poly in faces)
                    for (int i = 0; i < poly.Corners.Length; i++)
                        poly.Corners[i].Point = Copy(poly.Corners[i].Point);
                foreach (var (a, b, part) in sides)
                    AddQuad(a, b, Copy(b.Point), Copy(a.Point), part);
            }
            else
                foreach (var edge in edges)
                {
                    int owner = _edgePolys[edge][0];
                    var corners = _polys[owner].Corners;
                    var a = corners.First(c => c.Point == edge.A);
                    var b = corners.First(c => c.Point == edge.B);
                    AddQuad(a, b, Copy(edge.B), Copy(edge.A), _polys[owner].Part);
                }
            Selection = copies.Values.ToHashSet();
        });
        return direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) : Vector3.UnitY;
    }

    // Makes a smaller copy of each selected face inside it, joined to the original edge by a ring of faces. amount is how far toward
    // the middle, from 0 to 1.
    public string? Inset(float amount)
    {
        var faces = SelectedPolys();
        if (faces.Count == 0) return "Select a face to inset.";
        ChangeShape(() =>
        {
            var inner = new HashSet<int>();
            foreach (var poly in faces)
            {
                var center = FaceCenter(poly);
                var outer = (Corner[])poly.Corners.Clone();
                var moved = new int[outer.Length];
                for (int i = 0; i < outer.Length; i++)
                {
                    moved[i] = _points.Count;
                    _points.Add(Vector3.Lerp(_points[outer[i].Point], center, amount));
                    poly.Corners[i].Point = moved[i];
                    inner.Add(moved[i]);
                }
                for (int i = 0; i < outer.Length; i++)
                {
                    int next = (i + 1) % outer.Length;
                    AddQuad(outer[i], outer[next], moved[next], moved[i], poly.Part);
                }
            }
            Selection = inner;
        });
        return null;
    }

    public string? Delete(DeleteKind kind)
    {
        Func<Poly, bool> doomed = kind switch
        {
            DeleteKind.Vertices => p => p.Corners.Any(c => Selection.Contains(c.Point)),
            DeleteKind.Faces => IsSelected,
            _ => p => HasSelectedEdge(p),
        };
        if (!_polys.Any(doomed)) return "Nothing selected to delete.";
        ChangeShape(() =>
        {
            _polys.RemoveAll(new Predicate<Poly>(doomed));
            Selection.Clear();
        });
        return null;
    }

    // Brings the selected points together at their center.
    public string? Merge()
    {
        if (Selection.Count < 2) return "Select two or more vertices to merge.";
        ChangeShape(() =>
        {
            int kept = Selection.First();
            _points[kept] = Center(Selection);
            Join(Selection.ToDictionary(p => p, _ => kept));
            Selection = [kept];
        });
        return null;
    }

    // Joins points that are in the same place, within distance. Returns how many went.
    public int RemoveDoubles(float distance)
    {
        var chosen = Selection.Count > 1 ? Selection.ToList() : Enumerable.Range(0, _points.Count).ToList();
        var targets = new Dictionary<int, int>();
        var cells = new Dictionary<(int, int, int), List<int>>();
        float size = MathF.Max(distance, 1e-7f);
        foreach (int point in chosen)
        {
            var at = _points[point];
            var cell = ((int)MathF.Floor(at.X / size), (int)MathF.Floor(at.Y / size), (int)MathF.Floor(at.Z / size));
            int match = -1;
            for (int x = -1; x <= 1 && match < 0; x++)
                for (int y = -1; y <= 1 && match < 0; y++)
                    for (int z = -1; z <= 1 && match < 0; z++)
                        if (cells.TryGetValue((cell.Item1 + x, cell.Item2 + y, cell.Item3 + z), out var near))
                            match = near.FirstOrDefault(n => Vector3.Distance(_points[n], at) <= distance, -1);
            if (match >= 0)
                targets[point] = match;
            else
            {
                if (!cells.TryGetValue(cell, out var list))
                    cells[cell] = list = new();
                list.Add(point);
            }
        }
        if (targets.Count > 0)
            ChangeShape(() => Join(targets), allNormals: true);
        return targets.Count;
    }

    // Makes a face from the selected points, in order around their center.
    public string? Fill()
    {
        if (Selection.Count < 3) return "Select three or more vertices to make a face.";
        var points = Selection.ToList();
        var center = Center(points);

        // The plane they lie in comes from the two directions they spread furthest along.
        var first = points.Select(p => _points[p] - center).MaxBy(v => v.LengthSquared());
        var second = points.Select(p => Vector3.Cross(first, _points[p] - center)).MaxBy(v => v.LengthSquared());
        if (second.LengthSquared() < 1e-12f) return "These vertices are in a line, so they can't make a face.";
        var normal = Vector3.Normalize(second);
        var across = Vector3.Normalize(first);
        var up = Vector3.Cross(normal, across);
        var ordered = points.OrderBy(p => MathF.Atan2(Vector3.Dot(_points[p] - center, up), Vector3.Dot(_points[p] - center, across))).ToList();

        // Facing away from the middle of the model reads as outward.
        if (_polys.Count > 0 && Vector3.Dot(normal, center - Center(Enumerable.Range(0, _points.Count))) < 0f)
            ordered.Reverse();
        ChangeShape(() =>
        {
            int key = _nextKey++;
            _polys.Add(new Poly(ordered.Select(p => new Corner { Point = p, Normal = normal, Color = Vector4.One, Key = key }).ToArray(), 0));
        });
        return null;
    }

    // Splits each selected face into four-sided faces around its middle. Neighbors get a corner where their edge was split, so the
    // surface stays closed.
    public string? Subdivide()
    {
        var faces = SelectedPolys();
        if (faces.Count == 0) return "Select a face to subdivide.";
        ChangeShape(() =>
        {
            var chosen = faces.ToHashSet();
            var middles = new Dictionary<(int, int), int>();
            int Middle(int a, int b)
            {
                var edge = EdgeKey(a, b);
                if (middles.TryGetValue(edge, out int point)) return point;
                middles[edge] = point = _points.Count;
                _points.Add((_points[a] + _points[b]) * 0.5f);
                return point;
            }

            var added = new List<Poly>();
            var fresh = new HashSet<int>();
            foreach (var poly in faces)
            {
                var corners = poly.Corners;
                int count = corners.Length;
                int centerPoint = _points.Count;
                _points.Add(FaceCenter(poly));
                fresh.Add(centerPoint);
                int key = corners[0].Key;
                var centerCorner = Mixed(corners, centerPoint, key);
                var mids = new Corner[count];
                for (int i = 0; i < count; i++)
                {
                    var next = corners[(i + 1) % count];
                    mids[i] = Between(corners[i], next, Middle(corners[i].Point, next.Point));
                    fresh.Add(mids[i].Point);
                }
                for (int i = 0; i < count; i++)
                    added.Add(new Poly([corners[i], mids[i], centerCorner, mids[(i + count - 1) % count]], poly.Part));
            }
            _polys.RemoveAll(chosen.Contains);
            foreach (var poly in _polys)
                InsertMiddles(poly, middles);
            _polys.AddRange(added);
            Selection.UnionWith(fresh);
        });
        return null;
    }

    // Adds a line of edges through the strip of four-sided faces that crosses this edge. The new points are left selected.
    public string? LoopCut(int a, int b)
    {
        var ring = EdgeRing(a, b).ToHashSet();
        var strip = _polys.Where(p => p.Corners.Length == 4 && Enumerable.Range(0, 4).Any(i =>
            ring.Contains(EdgeKey(p.Corners[i].Point, p.Corners[(i + 1) % 4].Point)) &&
            ring.Contains(EdgeKey(p.Corners[(i + 2) % 4].Point, p.Corners[(i + 3) % 4].Point)))).ToList();
        if (strip.Count == 0) return "There are no four-sided faces across this edge to cut.";

        ChangeShape(() =>
        {
            var middles = new Dictionary<(int, int), int>();
            int Middle((int, int) edge)
            {
                if (middles.TryGetValue(edge, out int point)) return point;
                middles[edge] = point = _points.Count;
                _points.Add((_points[edge.Item1] + _points[edge.Item2]) * 0.5f);
                return point;
            }

            var added = new List<Poly>();
            foreach (var poly in strip)
            {
                var c = poly.Corners;
                int first = Enumerable.Range(0, 4).First(i => ring.Contains(EdgeKey(c[i].Point, c[(i + 1) % 4].Point)) &&
                                                              ring.Contains(EdgeKey(c[(i + 2) % 4].Point, c[(i + 3) % 4].Point)));
                var (p0, p1, p2, p3) = (c[first], c[(first + 1) % 4], c[(first + 2) % 4], c[(first + 3) % 4]);
                var near = Between(p0, p1, Middle(EdgeKey(p0.Point, p1.Point)));
                var far = Between(p2, p3, Middle(EdgeKey(p2.Point, p3.Point)));
                added.Add(new Poly([p0, near, far, p3], poly.Part));
                added.Add(new Poly([near, p1, p2, far], poly.Part));
            }
            var cut = strip.ToHashSet();
            _polys.RemoveAll(cut.Contains);
            foreach (var poly in _polys)
                InsertMiddles(poly, middles);
            _polys.AddRange(added);
            Selection = middles.Values.ToHashSet();
        });
        return null;
    }

    // Copies the selected faces. The copies are left selected, in the same place.
    public string? Duplicate()
    {
        var faces = SelectedPolys();
        if (faces.Count == 0) return "Select faces to duplicate.";
        ChangeShape(() =>
        {
            var copies = new Dictionary<int, int>();
            foreach (var poly in faces)
            {
                var corners = (Corner[])poly.Corners.Clone();
                for (int i = 0; i < corners.Length; i++)
                {
                    if (!copies.TryGetValue(corners[i].Point, out int copy))
                    {
                        copies[corners[i].Point] = copy = _points.Count;
                        _points.Add(_points[corners[i].Point]);
                    }
                    corners[i].Point = copy;
                }
                _polys.Add(new Poly(corners, poly.Part));
            }
            Selection = copies.Values.ToHashSet();
        });
        return null;
    }

    // Reflects the selected points across their center along an axis (0 for X, 1 for Y, 2 for Z).
    public string? Mirror(int axis)
    {
        if (Selection.Count == 0) return "Select something to mirror.";
        ChangeShape(() =>
        {
            var center = Center(Selection);
            var flip = Vector3.One;
            flip[axis] = -1f;
            foreach (int point in Selection)
                _points[point] = center + (_points[point] - center) * flip;
            foreach (var poly in SelectedPolys())
                Array.Reverse(poly.Corners);
        });
        return null;
    }

    // ---- normals -------------------------------------------------------------------------------

    public string? FlipNormals()
    {
        var faces = SelectedPolys();
        if (faces.Count == 0) return "Select faces to flip.";
        ChangeShape(() =>
        {
            foreach (var poly in faces)
                Array.Reverse(poly.Corners);
        }, allNormals: true);
        return null;
    }

    // Turns every face of each connected piece the same way, and that way outward.
    public void RecalculateNormals()
    {
        ChangeShape(() =>
        {
            var visited = new bool[_polys.Count];
            for (int start = 0; start < _polys.Count; start++)
            {
                if (visited[start]) continue;
                var piece = new List<int> { start };
                visited[start] = true;
                for (int i = 0; i < piece.Count; i++)
                {
                    var corners = _polys[piece[i]].Corners;
                    for (int c = 0; c < corners.Length; c++)
                    {
                        int a = corners[c].Point, b = corners[(c + 1) % corners.Length].Point;
                        foreach (int next in _edgePolys[EdgeKey(a, b)])
                        {
                            if (visited[next]) continue;
                            visited[next] = true;
                            piece.Add(next);
                            if (Runs(_polys[next], a, b))
                                Array.Reverse(_polys[next].Corners);
                        }
                    }
                }

                // A closed surface encloses positive volume when it faces outward.
                float volume = 0f;
                foreach (int index in piece)
                {
                    var corners = _polys[index].Corners;
                    var origin = _points[corners[0].Point];
                    for (int c = 1; c + 1 < corners.Length; c++)
                        volume += Vector3.Dot(origin, Vector3.Cross(_points[corners[c].Point], _points[corners[c + 1].Point]));
                }
                if (volume < 0f)
                    foreach (int index in piece)
                        Array.Reverse(_polys[index].Corners);
            }
        }, allNormals: true);
    }

    public string? ShadeSmooth() => Shade(_ => 0);

    public string? ShadeFlat() => Shade(_ => _nextKey++);

    private string? Shade(Func<Poly, int> keyFor)
    {
        var faces = SelectedPolys();
        if (faces.Count == 0) return "Select faces to shade.";
        ChangeShape(() =>
        {
            foreach (var poly in faces)
            {
                int key = keyFor(poly);
                for (int i = 0; i < poly.Corners.Length; i++)
                    poly.Corners[i].Key = key;
            }
        });
        return null;
    }

    // ---- moving points -------------------------------------------------------------------------

    // Moves each selected point part of the way to the middle of its neighbors.
    public void SmoothVertices(float amount)
    {
        var targets = Selection.Where(p => _neighbors[p].Count > 0)
            .Select(p => (p, Vector3.Lerp(_points[p], Center(_neighbors[p]), amount))).ToList();
        MovePoints(targets);
    }

    // Moves each selected point by a random amount, up to this far.
    public void Randomize(float distance, int seed)
    {
        var random = new Random(seed);
        float Next() => (random.NextSingle() * 2f - 1f) * distance;
        MovePoints(Selection.Select(p => (p, _points[p] + new Vector3(Next(), Next(), Next()))).ToList());
    }

    // Pushes the selected points out onto a sphere around their center.
    public void ToSphere()
    {
        var center = Center(Selection);
        float radius = Selection.Count == 0 ? 0f : Selection.Average(p => Vector3.Distance(_points[p], center));
        MovePoints(Selection.Where(p => _points[p] != center)
            .Select(p => (p, center + Vector3.Normalize(_points[p] - center) * radius)).ToList());
    }

    // Moves the selected points onto the plane that fits them best.
    public void Flatten()
    {
        var normal = Vector3.Zero;
        foreach (var poly in SelectedPolys())
            normal += FaceNormal(poly);
        if (normal.LengthSquared() < 1e-12f) return;
        normal = Vector3.Normalize(normal);
        var center = Center(Selection);
        MovePoints(Selection.Select(p => (p, _points[p] - normal * Vector3.Dot(_points[p] - center, normal))).ToList());
    }

    private void MovePoints(List<(int Point, Vector3 Position)> moves)
    {
        if (moves.Count == 0) return;
        BeginChange(moves.Select(m => m.Point));
        foreach (var (point, position) in moves)
            SetPoint(point, position);
        UpdateNormals(moves.Select(m => m.Point).ToList());
        EndChange();
    }

    // ---- helpers -------------------------------------------------------------------------------

    private List<Poly> SelectedPolys() => _polys.Where(IsSelected).ToList();

    private bool HasSelectedEdge(Poly poly)
    {
        var corners = poly.Corners;
        for (int i = 0; i < corners.Length; i++)
            if (Selection.Contains(corners[i].Point) && Selection.Contains(corners[(i + 1) % corners.Length].Point))
                return true;
        return false;
    }

    private static bool Runs(Poly poly, int a, int b)
    {
        var corners = poly.Corners;
        for (int i = 0; i < corners.Length; i++)
            if (corners[i].Point == a && corners[(i + 1) % corners.Length].Point == b)
                return true;
        return false;
    }

    // Points each key's corners at its value, dropping the corners that end up next to their own copy.
    private void Join(Dictionary<int, int> targets)
    {
        foreach (var poly in _polys)
        {
            var corners = new List<Corner>(poly.Corners.Length);
            foreach (var corner in poly.Corners)
            {
                var moved = corner;
                moved.Point = targets.GetValueOrDefault(corner.Point, corner.Point);
                if (corners.Count == 0 || corners[^1].Point != moved.Point)
                    corners.Add(moved);
            }
            if (corners.Count > 1 && corners[0].Point == corners[^1].Point)
                corners.RemoveAt(corners.Count - 1);
            poly.Corners = corners.ToArray();
        }
    }

    // A side face from two corners of an edge and the points they were copied to. It gets its own key, so its edges are hard.
    private void AddQuad(Corner a, Corner b, int bTop, int aTop, int part)
    {
        int key = _nextKey++;
        Corner With(Corner corner, int point)
        {
            corner.Point = point;
            corner.Key = key;
            return corner;
        }
        _polys.Add(new Poly([With(a, a.Point), With(b, b.Point), With(b, bTop), With(a, aTop)], part));
    }

    internal static Corner Between(Corner a, Corner b, int point) => new()
    {
        Point = point,
        Normal = a.Normal,
        Uv = (a.Uv + b.Uv) * 0.5f,
        Color = (a.Color + b.Color) * 0.5f,
        Key = Math.Min(a.Key, b.Key),   // the two faces on a smooth edge have the same pair of keys, so they agree
    };

    internal static Corner Mixed(Corner[] corners, int point, int key)
    {
        var uv = Vector2.Zero;
        var color = Vector4.Zero;
        foreach (var corner in corners)
        {
            uv += corner.Uv;
            color += corner.Color;
        }
        return new Corner { Point = point, Normal = corners[0].Normal, Uv = uv / corners.Length, Color = color / corners.Length, Key = key };
    }

    // Gives a face a corner at each of its edges that was split, so it stays joined to what was cut.
    private static void InsertMiddles(Poly poly, Dictionary<(int, int), int> middles)
    {
        var corners = new List<Corner>(poly.Corners.Length);
        for (int i = 0; i < poly.Corners.Length; i++)
        {
            var a = poly.Corners[i];
            var b = poly.Corners[(i + 1) % poly.Corners.Length];
            corners.Add(a);
            if (middles.TryGetValue(EdgeKey(a.Point, b.Point), out int point))
                corners.Add(Between(a, b, point));
        }
        if (corners.Count != poly.Corners.Length)
            poly.Corners = corners.ToArray();
    }
}
