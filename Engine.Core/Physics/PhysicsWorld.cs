using System.Numerics;
using Engine.Core.Scene;
using Engine.Core.Scripting;

namespace Engine.Core.Physics;

// Rigid body physics for one scene. Runs at a fixed 60 steps per second however fast frames come.
// Each step rebuilds bodies from the scene, so scripts may move objects or change components at any time:
//   - an object with a Rigidbody is a body; colliders on it and on its children (without their own Rigidbody) are its shapes
//   - an object with a collider and no Rigidbody above it is static
// Contacts are solved with sequential impulses (normal, two friction directions) and a small position correction.
public sealed partial class PhysicsWorld
{
    public const float FixedStep = 1f / 60f;
    private const int MaxStepsPerFrame = 5;    // keeps a slow frame from spiralling
    private const float Slop = 0.005f;         // allowed overlap, so resting contacts do not jitter
    private const float Correction = 0.2f;     // share of the overlap removed per step
    private const float MaxCorrectionSpeed = 4f;
    private const float BounceThreshold = 0.5f; // slower impacts do not bounce
    private const float MatchDistance = 0.05f;  // a contact this close to last step's reuses its impulses

    public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);
    public int SolverIterations { get; set; } = 10;

    private float _accumulator;
    private HashSet<(Guid, Guid)> _touching = new();
    private Dictionary<(Guid, Guid), List<Contact>> _previous = new();

    // Forgets contact history, so a scene that was replaced does not raise collision events for objects that are gone.
    public void Reset()
    {
        _accumulator = 0f;
        _touching = new();
        _previous = new();
    }

    // Advances the simulation by dt seconds of game time, in fixed steps.
    public void Update(Scene.Scene scene, float dt)
    {
        _accumulator = MathF.Min(_accumulator + dt, FixedStep * MaxStepsPerFrame);
        while (_accumulator >= FixedStep)
        {
            Step(scene, FixedStep);
            _accumulator -= FixedStep;
        }
    }

    // One fixed step: forces, collisions, then movement.
    public void Step(Scene.Scene scene, float h)
    {
        var bodies = BuildBodies(scene);

        foreach (var b in bodies)
            b.IntegrateVelocity(Gravity, h);

        var contacts = new List<Contact>();
        var touching = new HashSet<(Guid, Guid)>();
        var byPair = new Dictionary<(Guid, Guid), List<Contact>>();
        for (int i = 0; i < bodies.Count; i++)
            for (int j = i + 1; j < bodies.Count; j++)
            {
                var a = bodies[i];
                var b = bodies[j];
                if (a.InvMass == 0f && b.InvMass == 0f) continue;
                if (Vector3.DistanceSquared(a.BoundsCenter, b.BoundsCenter) >
                    (a.BoundsRadius + b.BoundsRadius) * (a.BoundsRadius + b.BoundsRadius))
                    continue;

                int before = contacts.Count;
                foreach (var sa in a.Shapes)
                    foreach (var sb in b.Shapes)
                        Collide(a, sa, b, sb, contacts);
                if (contacts.Count > before)
                {
                    touching.Add(Key(a.Object, b.Object));
                    var pair = (a.Object.Id, b.Object.Id);
                    var current = contacts.GetRange(before, contacts.Count - before);
                    byPair[pair] = current;
                    if (_previous.TryGetValue(pair, out var old))
                        foreach (var c in current)
                            c.WarmFrom(old);
                }
            }
        _previous = byPair;

        foreach (var c in contacts)
            c.Prepare(h);
        for (int it = 0; it < SolverIterations; it++)
            foreach (var c in contacts)
                c.Solve();

        foreach (var b in bodies)
            b.IntegratePosition(h);

        RaiseEvents(scene, touching);
    }

    // ---- bodies ----------------------------------------------------------------------

    private static List<Body> BuildBodies(Scene.Scene scene)
    {
        var byOwner = new Dictionary<SceneObject, Body>();
        foreach (var obj in scene.All)
        {
            var colliders = obj.Components.OfType<Collider>().ToList();
            var rb = obj.Components.OfType<Rigidbody>().FirstOrDefault();
            if (colliders.Count == 0 && rb is null) continue;

            var owner = rb is not null ? obj : OwnerAbove(obj) ?? obj;
            if (!byOwner.TryGetValue(owner, out var body))
                byOwner[owner] = body = new Body(owner, owner.Components.OfType<Rigidbody>().FirstOrDefault());
            bool dynamicOwner = body.Rigidbody is { IsKinematic: false };

            var world = obj.WorldMatrix;
            foreach (var collider in colliders)
            {
                if (collider is Terrain terrain)
                {
                    // The ground never moves, even when something on the same object makes the body dynamic.
                    if (terrain.Collide && Shape.FromTerrain(terrain, world) is { } groundShape)
                        body.Shapes.Add(groundShape);
                    continue;
                }
                if (collider is MeshCollider mesh)
                {
                    // Moving bodies collide as the mesh's convex hull; static and kinematic ones use its triangles.
                    if ((dynamicOwner ? Shape.FromHull(mesh, obj, world) : Shape.FromMesh(mesh, obj, world)) is { } meshShape)
                        body.Shapes.Add(meshShape);
                    continue;
                }
                if (Shape.From(collider, world) is { } shape)
                    body.Shapes.Add(shape);
            }
        }

        var bodies = byOwner.Values.Where(b => b.Shapes.Count > 0 || b.Rigidbody is not null).ToList();
        foreach (var b in bodies)
            b.Finish();
        return bodies;
    }

    private static SceneObject? OwnerAbove(SceneObject obj)
    {
        for (var p = obj.Parent; p is not null; p = p.Parent)
            if (p.Components.OfType<Rigidbody>().Any())
                return p;
        return null;
    }

    private sealed class Body
    {
        public readonly SceneObject Object;
        public readonly Rigidbody? Rigidbody;
        public readonly List<Shape> Shapes = new();
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        public float InvMass;
        public Vector3 InvInertia;   // diagonal, in the body's local frame
        public Vector3 BoundsCenter;
        public float BoundsRadius;

        public Body(SceneObject obj, Rigidbody? rb)
        {
            Object = obj;
            Rigidbody = rb;
            Decompose(obj.WorldMatrix, out _, out Rotation, out Position);
        }

        public bool IsDynamic => InvMass > 0f;

        public void Finish()
        {
            var rb = Rigidbody;
            if (rb is { IsKinematic: false } && rb.Mass > 0f && float.IsFinite(rb.Mass))
            {
                InvMass = 1f / rb.Mass;
                Velocity = rb.Velocity;
                AngularVelocity = rb.FreezeRotation ? Vector3.Zero : rb.AngularVelocity;
                InvInertia = rb.FreezeRotation ? Vector3.Zero : InverseInertia(rb.Mass);
            }

            if (Shapes.Count == 0)
            {
                BoundsCenter = Position;
                BoundsRadius = 0f;
                return;
            }
            BoundsCenter = Shapes.Aggregate(Vector3.Zero, (s, sh) => s + sh.Center) / Shapes.Count;
            BoundsRadius = Shapes.Max(s => Vector3.Distance(s.Center, BoundsCenter) + s.BoundingRadius);
        }

        // Inertia of the first shape, measured in the body's own frame.
        private Vector3 InverseInertia(float mass)
        {
            Vector3 inertia;
            if (Shapes.FirstOrDefault() is { } s && (s.IsBox || s.IsHull))
            {
                var size = s.HalfExtents * 2f;
                inertia = mass / 12f * new Vector3(
                    size.Y * size.Y + size.Z * size.Z,
                    size.X * size.X + size.Z * size.Z,
                    size.X * size.X + size.Y * size.Y);
            }
            else
            {
                float r = Shapes.FirstOrDefault()?.Radius ?? 0.5f;
                inertia = new Vector3(0.4f * mass * r * r);
            }
            return new Vector3(Inv(inertia.X), Inv(inertia.Y), Inv(inertia.Z));
        }

        private static float Inv(float v) => v > 1e-9f ? 1f / v : 0f;

        // Inverse inertia applied to a world vector.
        public Vector3 ApplyInvInertia(Vector3 v)
        {
            if (InvInertia == Vector3.Zero) return Vector3.Zero;
            var local = Vector3.Transform(v, Quaternion.Conjugate(Rotation));
            return Vector3.Transform(local * InvInertia, Rotation);
        }

        public void IntegrateVelocity(Vector3 gravity, float h)
        {
            var rb = Rigidbody;
            if (rb is null || !IsDynamic)
            {
                rb?.ClearPending();   // kinematic and massless bodies are not pushed
                return;
            }

            if (rb.UseGravity) Velocity += gravity * h;
            Velocity += rb.PendingImpulse * InvMass + rb.PendingVelocity;
            if (!rb.FreezeRotation)
                AngularVelocity += ApplyInvInertia(rb.PendingAngularImpulse) + rb.PendingAngularVelocity;
            Velocity /= 1f + h * MathF.Max(0f, rb.Drag);
            AngularVelocity /= 1f + h * MathF.Max(0f, rb.AngularDrag);
            rb.ClearPending();
        }

        public void IntegratePosition(float h)
        {
            if (!IsDynamic || Rigidbody is not { } rb) return;

            if (!IsFinite(Velocity) || !IsFinite(AngularVelocity))
            {
                Velocity = Vector3.Zero;
                AngularVelocity = Vector3.Zero;
            }

            Position += Velocity * h;
            if (AngularVelocity != Vector3.Zero)
            {
                var spin = new Quaternion(AngularVelocity * (0.5f * h), 0f) * Rotation;
                Rotation = Quaternion.Normalize(new Quaternion(
                    Rotation.X + spin.X, Rotation.Y + spin.Y, Rotation.Z + spin.Z, Rotation.W + spin.W));
            }

            rb.Velocity = Velocity;
            rb.AngularVelocity = AngularVelocity;
            WriteBack();
        }

        // Moves the scene object to the simulated pose, keeping its scale. Children follow through the hierarchy.
        private void WriteBack()
        {
            if (Object.Parent is null)
            {
                Object.Position = Position;
                Object.Rotation = Rotation;
                return;
            }

            Decompose(Object.WorldMatrix, out var worldScale, out _, out _);
            var world = Matrix4x4.CreateScale(worldScale) * Matrix4x4.CreateFromQuaternion(Rotation) *
                        Matrix4x4.CreateTranslation(Position);
            if (!Matrix4x4.Invert(Object.Parent.WorldMatrix, out var toParent)) return;
            if (Matrix4x4.Decompose(world * toParent, out _, out var rotation, out var position))
            {
                Object.Position = position;
                Object.Rotation = Quaternion.Normalize(rotation);
            }
        }
    }

    // ---- shapes ----------------------------------------------------------------------

    private sealed partial class Shape
    {
        public bool IsBox;
        public Vector3 Center;
        public Quaternion Rotation;
        public Vector3 HalfExtents;   // boxes
        public float Radius;          // spheres
        public float Friction;
        public float Bounciness;
        public Vector3 AxisX, AxisY, AxisZ;

        public float BoundingRadius => IsMesh ? MeshRadius : IsBox ? HalfExtents.Length() : Radius;   // hulls keep theirs in Radius

        public Vector3 Axis(int i) => i switch { 0 => AxisX, 1 => AxisY, _ => AxisZ };
        public float Half(int i) => i switch { 0 => HalfExtents.X, 1 => HalfExtents.Y, _ => HalfExtents.Z };

        public static Shape? From(Collider collider, Matrix4x4 world)
        {
            Decompose(world, out var scale, out var rotation, out _);
            scale = Vector3.Abs(scale);
            var shape = new Shape
            {
                Center = Vector3.Transform(collider.Center, world),
                Rotation = rotation,
                Friction = Math.Clamp(collider.Friction, 0f, 2f),
                Bounciness = Math.Clamp(collider.Bounciness, 0f, 1f),
                AxisX = Vector3.Transform(Vector3.UnitX, rotation),
                AxisY = Vector3.Transform(Vector3.UnitY, rotation),
                AxisZ = Vector3.Transform(Vector3.UnitZ, rotation),
            };
            switch (collider)
            {
                case BoxCollider box:
                    shape.IsBox = true;
                    shape.HalfExtents = Vector3.Abs(box.Size) * scale * 0.5f;
                    if (shape.HalfExtents.X <= 0f || shape.HalfExtents.Y <= 0f || shape.HalfExtents.Z <= 0f) return null;
                    break;
                case SphereCollider sphere:
                    shape.Radius = MathF.Abs(sphere.Radius) * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
                    if (shape.Radius <= 0f) return null;
                    break;
                default:
                    return null;
            }
            return IsFinite(shape.Center) ? shape : null;
        }
    }

    // ---- narrow phase --------------------------------------------------------------------
    // Every test reports contacts with the normal pointing from body a toward body b.

    private static void Collide(Body a, Shape sa, Body b, Shape sb, List<Contact> contacts)
    {
        float friction = MathF.Sqrt(sa.Friction * sb.Friction);
        float bounce = MathF.Max(sa.Bounciness, sb.Bounciness);
        void Add(Vector3 point, Vector3 normal, float depth) =>
            contacts.Add(new Contact(a, b, point, normal, depth, friction, bounce));

        if (sa.IsMesh || sb.IsMesh)
        {
            // Mesh tests report normals from the mesh toward the other shape; flip when the mesh is body b.
            if (sa.IsMesh && sb.IsMesh) return;
            bool meshIsA = sa.IsMesh;
            MeshContacts(meshIsA ? sa : sb, meshIsA ? sb : sa, (p, n, d) => Add(p, meshIsA ? n : -n, d));
        }
        else if (sa.IsHull || sb.IsHull)
        {
            if (!sa.IsBox && !sa.IsHull) SpherePoly(sa, Poly.FromHull(sb), (p, n, d) => Add(p, -n, d));
            else if (!sb.IsBox && !sb.IsHull) SpherePoly(sb, Poly.FromHull(sa), Add);
            else PolyPoly(AsPoly(sa), AsPoly(sb), Add);
        }
        else if (!sa.IsBox && !sb.IsBox) SphereSphere(sa, sb, Add);
        else if (!sa.IsBox) SphereBox(sa, sb, Add, flip: false);
        else if (!sb.IsBox) SphereBox(sb, sa, Add, flip: true);
        else BoxBox(sa, sb, Add);
    }

    private delegate void AddContact(Vector3 point, Vector3 normal, float depth);

    private static Poly AsPoly(Shape s) => s.IsHull ? Poly.FromHull(s) : Poly.FromBox(s);

    private static void SphereSphere(Shape a, Shape b, AddContact add)
    {
        var d = b.Center - a.Center;
        float dist = d.Length();
        float depth = a.Radius + b.Radius - dist;
        if (depth <= 0f) return;
        var n = dist > 1e-6f ? d / dist : Vector3.UnitY;
        add(a.Center + n * (a.Radius - depth * 0.5f), n, depth);
    }

    // The sphere is s and the box is box. With flip false the sphere is body a, otherwise the box is.
    private static void SphereBox(Shape s, Shape box, AddContact add, bool flip)
    {
        var inv = Quaternion.Conjugate(box.Rotation);
        var local = Vector3.Transform(s.Center - box.Center, inv);
        var h = box.HalfExtents;
        var closest = Vector3.Clamp(local, -h, h);

        Vector3 normalLocal;   // from the sphere toward the box
        Vector3 pointLocal;
        float depth;
        if (closest != local)
        {
            var d = closest - local;
            float dist = d.Length();
            if (dist >= s.Radius) return;
            normalLocal = d / dist;
            pointLocal = closest;
            depth = s.Radius - dist;
        }
        else
        {
            // The sphere's center is inside the box: push out through the nearest face.
            var gap = h - Vector3.Abs(local);
            int axis = gap.X < gap.Y ? (gap.X < gap.Z ? 0 : 2) : (gap.Y < gap.Z ? 1 : 2);
            float sign = Component(local, axis) >= 0f ? 1f : -1f;
            normalLocal = -sign * Unit(axis);
            pointLocal = local;
            SetComponent(ref pointLocal, axis, sign * Component(h, axis));
            depth = s.Radius + Component(gap, axis);
        }

        var normal = Vector3.Transform(normalLocal, box.Rotation);
        var point = box.Center + Vector3.Transform(pointLocal, box.Rotation);
        add(point, flip ? -normal : normal, depth);
    }

    // Separating axis test over the 15 box axes, then contact points by clipping faces or by closest edge points.
    private static void BoxBox(Shape a, Shape b, AddContact add)
    {
        var t = b.Center - a.Center;
        float bestFace = float.MaxValue, bestEdge = float.MaxValue;
        int faceAxis = -1, edgeA = -1, edgeB = -1;
        Vector3 faceNormal = default, edgeNormal = default;

        for (int i = 0; i < 6; i++)
        {
            var axis = i < 3 ? a.Axis(i) : b.Axis(i - 3);
            float overlap = Overlap(a, b, axis, t);
            if (overlap < 0f) return;
            if (overlap < bestFace)
            {
                bestFace = overlap;
                faceAxis = i;
                faceNormal = Vector3.Dot(axis, t) < 0f ? -axis : axis;
            }
        }

        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                var axis = Vector3.Cross(a.Axis(i), b.Axis(j));
                float length = axis.Length();
                if (length < 1e-4f) continue;   // parallel edges are covered by the face axes
                axis /= length;
                float overlap = Overlap(a, b, axis, t);
                if (overlap < 0f) return;
                if (overlap < bestEdge)
                {
                    bestEdge = overlap;
                    edgeA = i;
                    edgeB = j;
                    edgeNormal = Vector3.Dot(axis, t) < 0f ? -axis : axis;
                }
            }

        // Faces win unless an edge axis is clearly shallower, which keeps resting boxes stable.
        if (edgeA >= 0 && bestEdge < bestFace * 0.95f - 0.01f)
        {
            EdgeContact(a, b, edgeA, edgeB, edgeNormal, bestEdge, add);
            return;
        }

        if (faceAxis < 3) FaceContacts(a, b, faceAxis, faceNormal, add, flip: false);
        else FaceContacts(b, a, faceAxis - 3, -faceNormal, add, flip: true);
    }

    private static float Overlap(Shape a, Shape b, Vector3 axis, Vector3 t)
    {
        float ra = a.HalfExtents.X * MathF.Abs(Vector3.Dot(a.AxisX, axis)) +
                   a.HalfExtents.Y * MathF.Abs(Vector3.Dot(a.AxisY, axis)) +
                   a.HalfExtents.Z * MathF.Abs(Vector3.Dot(a.AxisZ, axis));
        float rb = b.HalfExtents.X * MathF.Abs(Vector3.Dot(b.AxisX, axis)) +
                   b.HalfExtents.Y * MathF.Abs(Vector3.Dot(b.AxisY, axis)) +
                   b.HalfExtents.Z * MathF.Abs(Vector3.Dot(b.AxisZ, axis));
        return ra + rb - MathF.Abs(Vector3.Dot(t, axis));
    }

    // reference's face along axis faces the incident box (normal points from reference to incident).
    // The incident box's most opposed face is clipped to the reference face's sides; points below it are contacts.
    private static void FaceContacts(Shape reference, Shape incident, int axis, Vector3 normal, AddContact add, bool flip)
    {
        // Incident face: the face of the other box that points most against the normal.
        int incAxis = 0;
        float best = -1f;
        for (int i = 0; i < 3; i++)
        {
            float d = MathF.Abs(Vector3.Dot(incident.Axis(i), normal));
            if (d > best) { best = d; incAxis = i; }
        }
        var incNormal = Vector3.Dot(incident.Axis(incAxis), normal) > 0f ? -incident.Axis(incAxis) : incident.Axis(incAxis);
        var incCenter = incident.Center + incNormal * incident.Half(incAxis);
        int u = (incAxis + 1) % 3, v = (incAxis + 2) % 3;
        var du = incident.Axis(u) * incident.Half(u);
        var dv = incident.Axis(v) * incident.Half(v);
        var polygon = new List<Vector3> { incCenter + du + dv, incCenter - du + dv, incCenter - du - dv, incCenter + du - dv };

        // Clip against the four side planes of the reference face.
        for (int k = 1; k <= 2; k++)
        {
            int side = (axis + k) % 3;
            var sideAxis = reference.Axis(side);
            float offset = Vector3.Dot(sideAxis, reference.Center);
            polygon = Clip(polygon, sideAxis, offset + reference.Half(side));
            polygon = Clip(polygon, -sideAxis, -offset + reference.Half(side));
            if (polygon.Count == 0) return;
        }

        var faceCenter = reference.Center + normal * reference.Half(axis);
        var points = new List<(Vector3 Point, float Depth)>();
        foreach (var p in polygon)
        {
            float separation = Vector3.Dot(p - faceCenter, normal);
            if (separation <= 0f)
                points.Add((p - normal * (separation * 0.5f), -separation));
        }

        foreach (var (point, depth) in Reduce(points))
            add(point, flip ? -normal : normal, depth);
    }

    private static List<Vector3> Clip(List<Vector3> polygon, Vector3 n, float d)
    {
        var result = new List<Vector3>(polygon.Count + 2);
        for (int i = 0; i < polygon.Count; i++)
        {
            var p = polygon[i];
            var q = polygon[(i + 1) % polygon.Count];
            float dp = Vector3.Dot(n, p) - d, dq = Vector3.Dot(n, q) - d;
            if (dp <= 0f) result.Add(p);
            if ((dp < 0f && dq > 0f) || (dp > 0f && dq < 0f))
                result.Add(p + (q - p) * (dp / (dp - dq)));
        }
        return result;
    }

    // Keeps at most four contacts: the deepest, then each next point farthest from the ones kept.
    private static List<(Vector3 Point, float Depth)> Reduce(List<(Vector3 Point, float Depth)> points)
    {
        if (points.Count <= 4) return points;
        var kept = new List<(Vector3 Point, float Depth)> { points.MaxBy(p => p.Depth) };
        while (kept.Count < 4)
            kept.Add(points.Where(p => !kept.Contains(p))
                           .MaxBy(p => kept.Min(k => Vector3.DistanceSquared(k.Point, p.Point))));
        return kept;
    }

    private static void EdgeContact(Shape a, Shape b, int i, int j, Vector3 n, float depth, AddContact add)
    {
        // The edge of a parallel to its axis i that is furthest along n, and the edge of b furthest against n.
        var pa = a.Center;
        var pb = b.Center;
        for (int k = 0; k < 3; k++)
        {
            if (k != i) pa += a.Axis(k) * (a.Half(k) * MathF.Sign(Vector3.Dot(a.Axis(k), n)));
            if (k != j) pb -= b.Axis(k) * (b.Half(k) * MathF.Sign(Vector3.Dot(b.Axis(k), n)));
        }
        var da = a.Axis(i);
        var db = b.Axis(j);

        // Closest points of the two edge lines.
        var r = pa - pb;
        float dot = Vector3.Dot(da, db);
        float denom = 1f - dot * dot;
        float s = 0f, u = 0f;
        if (denom > 1e-6f)
        {
            float c = Vector3.Dot(da, r), f = Vector3.Dot(db, r);
            s = Math.Clamp((dot * f - c) / denom, -a.Half(i), a.Half(i));
            u = Math.Clamp((dot * s + f), -b.Half(j), b.Half(j));
        }
        add((pa + da * s + pb + db * u) * 0.5f, n, depth);
    }

    // ---- contacts --------------------------------------------------------------------------

    private sealed class Contact
    {
        private readonly Body _a, _b;
        private readonly Vector3 _point, _normal;
        private readonly float _depth, _friction, _bounce;
        private Vector3 _ra, _rb, _t1, _t2;
        private float _normalMass, _t1Mass, _t2Mass, _bias;
        private float _pn, _pt1, _pt2;

        public Contact(Body a, Body b, Vector3 point, Vector3 normal, float depth, float friction, float bounce)
        {
            _a = a; _b = b; _point = point; _normal = normal; _depth = depth; _friction = friction; _bounce = bounce;
        }

        // Starts from the impulses of last step's contact at about the same point, so stacks settle instead of drifting.
        public void WarmFrom(List<Contact> previous)
        {
            foreach (var p in previous)
            {
                if (Vector3.DistanceSquared(p._point, _point) > MatchDistance * MatchDistance) continue;
                if (Vector3.Dot(p._normal, _normal) < 0.95f) continue;
                _pn = p._pn;
                _pt1 = p._pt1;
                _pt2 = p._pt2;
                return;
            }
        }

        public void Prepare(float h)
        {
            _ra = _point - _a.Position;
            _rb = _point - _b.Position;
            Tangents(_normal, out _t1, out _t2);
            _normalMass = Mass(_normal);
            _t1Mass = Mass(_t1);
            _t2Mass = Mass(_t2);

            float vn = Vector3.Dot(RelativeVelocity(), _normal);
            float push = MathF.Min(Correction / h * MathF.Max(0f, _depth - Slop), MaxCorrectionSpeed);
            float bounce = vn < -BounceThreshold ? -_bounce * vn : 0f;
            _bias = MathF.Max(push, bounce);

            Apply(_normal * _pn + _t1 * _pt1 + _t2 * _pt2);
        }

        public void Solve()
        {
            // Normal impulse: stop the bodies moving into each other, plus the push-out or bounce.
            float vn = Vector3.Dot(RelativeVelocity(), _normal);
            float lambda = _normalMass * (_bias - vn);
            float old = _pn;
            _pn = MathF.Max(0f, old + lambda);
            Apply(_normal * (_pn - old));

            // Friction impulses, limited by how hard the bodies press together.
            float limit = _friction * _pn;
            SolveFriction(_t1, _t1Mass, ref _pt1, limit);
            SolveFriction(_t2, _t2Mass, ref _pt2, limit);
        }

        private void SolveFriction(Vector3 tangent, float mass, ref float accumulated, float limit)
        {
            float vt = Vector3.Dot(RelativeVelocity(), tangent);
            float old = accumulated;
            accumulated = Math.Clamp(old - mass * vt, -limit, limit);
            Apply(tangent * (accumulated - old));
        }

        private Vector3 RelativeVelocity() =>
            _b.Velocity + Vector3.Cross(_b.AngularVelocity, _rb) - _a.Velocity - Vector3.Cross(_a.AngularVelocity, _ra);

        private float Mass(Vector3 dir)
        {
            float k = _a.InvMass + _b.InvMass +
                      Vector3.Dot(Vector3.Cross(_a.ApplyInvInertia(Vector3.Cross(_ra, dir)), _ra), dir) +
                      Vector3.Dot(Vector3.Cross(_b.ApplyInvInertia(Vector3.Cross(_rb, dir)), _rb), dir);
            return k > 1e-9f ? 1f / k : 0f;
        }

        private void Apply(Vector3 impulse)
        {
            if (_a.IsDynamic)
            {
                _a.Velocity -= impulse * _a.InvMass;
                _a.AngularVelocity -= _a.ApplyInvInertia(Vector3.Cross(_ra, impulse));
            }
            if (_b.IsDynamic)
            {
                _b.Velocity += impulse * _b.InvMass;
                _b.AngularVelocity += _b.ApplyInvInertia(Vector3.Cross(_rb, impulse));
            }
        }

        private static void Tangents(Vector3 n, out Vector3 t1, out Vector3 t2)
        {
            t1 = MathF.Abs(n.X) > 0.57f ? new Vector3(n.Y, -n.X, 0f) : new Vector3(0f, n.Z, -n.Y);
            t1 = Vector3.Normalize(t1);
            t2 = Vector3.Cross(n, t1);
        }
    }

    // ---- collision events -------------------------------------------------------------------

    private void RaiseEvents(Scene.Scene scene, HashSet<(Guid, Guid)> touching)
    {
        foreach (var pair in touching)
            if (!_touching.Contains(pair))
                Notify(scene, pair, enter: true);
        foreach (var pair in _touching)
            if (!touching.Contains(pair))
                Notify(scene, pair, enter: false);
        _touching = touching;
    }

    private static void Notify(Scene.Scene scene, (Guid A, Guid B) pair, bool enter)
    {
        var a = scene.Find(pair.A);
        var b = scene.Find(pair.B);
        if (a is null || b is null) return;
        ScriptRuntime.Collision(a, b, enter);
        ScriptRuntime.Collision(b, a, enter);
    }

    private static (Guid, Guid) Key(SceneObject a, SceneObject b) =>
        a.Id.CompareTo(b.Id) < 0 ? (a.Id, b.Id) : (b.Id, a.Id);

    // ---- math helpers -------------------------------------------------------------------------

    private static void Decompose(Matrix4x4 m, out Vector3 scale, out Quaternion rotation, out Vector3 position)
    {
        if (Matrix4x4.Decompose(m, out scale, out rotation, out position))
        {
            rotation = Quaternion.Normalize(rotation);
            return;
        }
        position = m.Translation;
        scale = new Vector3(
            new Vector3(m.M11, m.M12, m.M13).Length(),
            new Vector3(m.M21, m.M22, m.M23).Length(),
            new Vector3(m.M31, m.M32, m.M33).Length());
        rotation = Quaternion.Identity;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static Vector3 Unit(int axis) => axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
    private static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static void SetComponent(ref Vector3 v, int axis, float value)
    {
        switch (axis)
        {
            case 0: v.X = value; break;
            case 1: v.Y = value; break;
            default: v.Z = value; break;
        }
    }
}
