using Editor.Assistant;
using Editor;
using Editor.Ui;
using Editor.Terminal;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using EmpireHub;
using System.Numerics;
using System.Text;
using Engine.Core.Assets;
using Engine.Core.Audio;
using Engine.Core.Scene;
using Engine.Core.Scripting;
using Engine.Core.Serialization;

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

// ---- 1. Scene graph + .empirescene round trip -------------------------------------------
var dir = Path.Combine(Path.GetTempPath(), "empire-engine-test");
Directory.CreateDirectory(dir);

var scene = new Scene { Name = "Main" };
var root = new SceneObject { Name = "Crate", Position = new(1, 2, 3) };
root.Components.Add(new MeshRenderer { Model = "Models/crate.glb", Tint = new(1, 0.5f, 0, 1) });
var child = new SceneObject { Name = "Lid", Position = new(0, 1, 0), Scale = new(1, 0.1f, 1) };
child.Components.Add(new MeshRenderer { Model = "Models/lid.glb" });
scene.Add(root);
scene.Add(child, root);

var path = Path.Combine(dir, "Main.empirescene");
SceneSerializer.Save(scene, path);
var loaded = SceneSerializer.Load(path);

var loadedRoot = loaded.Find(root.Id);
var loadedChild = loaded.Find(child.Id);
Check(loaded.All.Count() == 2, "round trip keeps object count");
Check(loadedRoot is not null && loadedRoot.Name == "Crate" && loadedRoot.Position == new Vector3(1, 2, 3), "root transform and name survive");
Check(loadedChild?.Parent?.Id == root.Id, "parent link survives");
Check(loadedRoot!.Components.OfType<MeshRenderer>().Single().Tint == new Vector4(1, 0.5f, 0, 1), "component tint survives");
Check(loaded.Roots.Count() == 1, "hierarchy roots are correct");

// ---- 2. Undo-style remove/restore keeps the subtree --------------------------------------
loaded.Remove(loadedRoot!);
Check(loaded.All.Count() == 0, "remove takes the whole subtree out");
loaded.Add(loadedRoot!, null);
Check(loaded.All.Count() == 2 && loadedChild!.Parent == loadedRoot, "re-adding restores subtree and links");

// ---- 3. GLB loader on a generated binary glTF (one triangle + index buffer) -------------
byte[] MakeGlb()
{
    // BIN: 3 float positions (36 bytes), then 3 uint16 indices (6 bytes) padded to 4
    var bin = new MemoryStream();
    var bw = new BinaryWriter(bin);
    float[] pos = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
    foreach (var f in pos) bw.Write(f);
    ushort[] idx = { 0, 1, 2 };
    foreach (var i in idx) bw.Write(i);
    bw.Write((ushort)0);  // pad to 4 bytes
    byte[] binBytes = bin.ToArray();

    string json = """
    {
      "asset": { "version": "2.0" },
      "buffers": [ { "byteLength": 44 } ],
      "bufferViews": [
        { "buffer": 0, "byteOffset": 0,  "byteLength": 36 },
        { "buffer": 0, "byteOffset": 36, "byteLength": 6 }
      ],
      "accessors": [
        { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3",
          "min": [0, 0, 0], "max": [1, 1, 0] },
        { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR" }
      ],
      "meshes": [ { "name": "Tri", "primitives": [ { "attributes": { "POSITION": 0 }, "indices": 1 } ] } ]
    }
    """;
    byte[] jsonBytes = Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) & ~3, ' '));

    var ms = new MemoryStream();
    var w = new BinaryWriter(ms);
    int total = 12 + 8 + jsonBytes.Length + 8 + binBytes.Length;
    w.Write(0x46546C67u); w.Write(2u); w.Write((uint)total);
    w.Write((uint)jsonBytes.Length); w.Write(0x4E4F534Au); w.Write(jsonBytes);
    w.Write((uint)binBytes.Length); w.Write(0x004E4942u); w.Write(binBytes);
    return ms.ToArray();
}

var glbPath = Path.Combine(dir, "tri.glb");
File.WriteAllBytes(glbPath, MakeGlb());
var model = GlbLoader.Load(glbPath);
var mesh = model.Meshes.Single();
Check(model.Meshes.Count == 1 && mesh.Name == "Tri", "GLB mesh count and name");
Check(mesh.Positions.Length == 9 && mesh.Positions[3] == 1f, "GLB positions decode");
Check(mesh.Indices.SequenceEqual(new uint[] { 0, 1, 2 }), "GLB uint16 indices decode");
Check(mesh.Normals.Length == 9 && mesh.TexCoords.Length == 6, "missing attributes filled with zeros");
Check(model.BoundsMax == new Vector3(1, 1, 0) && model.BoundsMin == Vector3.Zero, "bounds from accessor min/max");

// ---- 4. Asset cache returns the same instance --------------------------------------------
var cache = new AssetCache(dir);
Check(ReferenceEquals(cache.GetModel("tri.glb"), cache.GetModel("tri.glb")), "asset cache reuses loaded models");

// ---- 5. Physics: falling, resting, stacking, saving ------------------------------------------
Scene PhysicsScene()
{
    var s = new Scene();
    var ground = new SceneObject { Name = "Ground", Position = new(0, -0.5f, 0) };
    ground.Components.Add(new BoxCollider { Size = new(20, 1, 20) });
    s.Add(ground);
    return s;
}
SceneObject Dynamic(Scene s, Vector3 position, Collider collider)
{
    var o = new SceneObject { Position = position };
    o.Components.Add(new Rigidbody());
    o.Components.Add(collider);
    s.Add(o);
    return o;
}
void Simulate(Scene s, float seconds)
{
    for (int i = 0; i < (int)(seconds * 60); i++) s.Physics.Step(s, 1f / 60f);
}

var fall = PhysicsScene();
var fallBox = Dynamic(fall, new(0, 3, 0), new BoxCollider());
var fallBall = Dynamic(fall, new(3, 3, 0), new SphereCollider());
Simulate(fall, 4);
Check(MathF.Abs(fallBox.Position.Y - 0.5f) < 0.03f, "box falls and rests on a static box collider");
Check(MathF.Abs(fallBall.Position.Y - 0.5f) < 0.03f, "sphere falls and rests on a static box collider");

var stack = PhysicsScene();
var stacked = Enumerable.Range(0, 5).Select(i => Dynamic(stack, new(0, 0.5f + i, 0), new BoxCollider())).ToList();
Simulate(stack, 8);
Check(stacked.Select((b, i) => MathF.Abs(b.Position.Y - (0.5f + i)) < 0.05f && MathF.Abs(b.Position.X) < 0.05f).All(ok => ok),
      "a stack of five boxes stays standing");

var saved = SceneSerializer.FromJson(SceneSerializer.ToJson(fall));
var savedBox = saved.Find(fallBox.Id)!;
Check(savedBox.Components.OfType<Rigidbody>().Count() == 1 && savedBox.Components.OfType<BoxCollider>().Count() == 1 &&
      saved.Find(fallBall.Id)!.Components.OfType<SphereCollider>().Single().Radius == 0.5f,
      "rigidbody and collider components survive save and load");

// The generated triangle (0,0,0) (1,0,0) (0,1,0), turned flat and scaled up, as a mesh collider floor.
Engine.Core.Physics.PhysicsWorld.ModelLoader = cache.GetModel;
var meshFloor = new Scene();
var floor = new SceneObject
{
    Name = "Floor",
    Scale = new(20, 20, 20),
    Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f),   // XY plane onto XZ
};
floor.Components.Add(new MeshRenderer { Model = "tri.glb" });
floor.Components.Add(new MeshCollider());
meshFloor.Add(floor);
var onMeshBall = Dynamic(meshFloor, new(3, 3, -3), new SphereCollider());
var onMeshBox = Dynamic(meshFloor, new(6, 3, -2), new BoxCollider());
Simulate(meshFloor, 4);
Check(MathF.Abs(onMeshBall.Position.Y - 0.5f) < 0.03f, "sphere rests on a mesh collider");
Check(MathF.Abs(onMeshBox.Position.Y - 0.5f) < 0.03f, "box rests flat on a mesh collider");
Check(SceneSerializer.FromJson(SceneSerializer.ToJson(meshFloor)).Find(floor.Id)!.Components.OfType<MeshCollider>().Count() == 1,
      "mesh collider survives save and load");

// A mesh collider on a moving rigidbody collides as its convex hull. The flat triangle becomes a thin slab.
var hullScene = PhysicsScene();
var tile = new SceneObject { Position = new(0, 2, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f) };
tile.Components.Add(new Rigidbody());
tile.Components.Add(new MeshRenderer { Model = "tri.glb" });
tile.Components.Add(new MeshCollider());
hullScene.Add(tile);
Simulate(hullScene, 3);
Check(tile.Position.Y > 0f && tile.Position.Y < 0.05f && tile.Components.OfType<Rigidbody>().Single().Velocity.Length() < 0.05f,
      "a mesh collider on a moving rigidbody lands and rests on the floor");

// ---- 6. Duplicating an object copies its subtree with new ids --------------------------------
var dupScene = new Scene();
var crate = new SceneObject { Name = "Crate", Position = new(1, 2, 3), Scale = new(2, 2, 2) };
crate.Components.Add(new MeshRenderer { Model = "Models/crate.glb", Tint = new(1, 0, 0, 1) });
crate.Components.Add(new BoxCollider { Size = new(1, 2, 3) });
var lid = new SceneObject { Name = "Lid", Position = new(0, 1, 0) };
dupScene.Add(crate);
dupScene.Add(lid, crate);

var copy = SceneSerializer.Duplicate(dupScene, crate);
var copyLid = copy.Children.SingleOrDefault();
Check(copy.Id != crate.Id && copy.Name == "Crate (1)" && copy.Parent is null, "duplicate gets a new id and the next free name");
Check(copy.Position == crate.Position && copy.Scale == crate.Scale, "duplicate keeps the transform");
Check(copy.Components.OfType<MeshRenderer>().Single().Tint == new Vector4(1, 0, 0, 1) &&
      copy.Components.OfType<BoxCollider>().Single().Size == new Vector3(1, 2, 3) &&
      !ReferenceEquals(copy.Components[0], crate.Components[0]), "duplicate has its own copies of the components");
Check(copyLid is not null && copyLid.Name == "Lid" && copyLid.Id != lid.Id && lid.Parent == crate,
      "children are duplicated under the copy and the original is untouched");
Check(dupScene.All.Count() == 4, "the scene holds the original and the copy");
Check(SceneSerializer.Duplicate(dupScene, crate).Name == "Crate (2)" &&
      SceneSerializer.Duplicate(dupScene, copy).Name == "Crate (3)", "copies of copies keep counting up");

// ---- 7. Public script fields: listed for the Inspector, saved, and applied before Start -------------
var described = ScriptFields.Describe(typeof(FieldsScript));
Check(string.Join(",", described.Select(f => $"{f.Name}={f.DefaultJson}")) == "speed=2.5,lives=3,title=\"Hi\",points=[1,2],names=[],notBuiltIn=[0,0,0]",
      "script fields list public built-in fields (and Vector3s) with their defaults");

var fieldScene = new Scene();
var fieldObj = new SceneObject { Name = "Player" };
var fieldComp = new ScriptComponent { ScriptName = "FieldsScript" };
fieldComp.Fields["speed"] = "7";
fieldComp.Fields["points"] = "[4,5,6]";
fieldComp.Fields["names"] = "[\"a\"]";
fieldObj.Components.Add(fieldComp);
fieldScene.Add(fieldObj);
var reloaded = SceneSerializer.FromJson(SceneSerializer.ToJson(fieldScene)).All.Single().Components.OfType<ScriptComponent>().Single();
var fieldsInstance = new FieldsScript();
Check(ScriptFields.Apply(fieldsInstance, reloaded.Fields) == "" && fieldsInstance.speed == 7f && fieldsInstance.lives == 3 &&
      fieldsInstance.points.SequenceEqual(new[] { 4f, 5f, 6f }) && fieldsInstance.names.SequenceEqual(new[] { "a" }),
      "saved field values load and apply; untouched fields keep their defaults");

// ---- 8. Particle systems ------------------------------------------------------------------------
var fxScene = new Scene();
var fxObj = new SceneObject { Name = "Fx" };
var fx = new ParticleSystem();
fx.Main.StartLifetime = 100f;
fx.Emission.Bursts.Add(new ParticleBurst { Time = 0.5f, Count = 40 });
fxObj.Components.Add(fx);
fxScene.Add(fxObj);
for (int i = 0; i < 60; i++) Particles.Update(fxScene, 1f / 60f);
Check(Math.Abs(fx.ParticleCount - 50) <= 1, $"particle system: 10/s for 1 s plus a burst of 40 gives about 50 (got {fx.ParticleCount})");
fx.Stop(clear: true);
fx.Emit(5);
Particles.Update(fxScene, 1f / 60f);
Check(fx.ParticleCount == 5 && !fx.IsEmitting, "Stop(clear) empties the system and Emit adds particles on demand");

fx.Renderer.Blend = ParticleBlend.Additive;
fx.Main.StartSpeed = new MinMax(2f, 4f);
var fxBack = SceneSerializer.FromJson(SceneSerializer.ToJson(fxScene)).All.Single().Components.OfType<ParticleSystem>().Single();
Check(fxBack.Renderer.Blend == ParticleBlend.Additive && fxBack.Main.StartSpeed.Max == 4f && fxBack.Emission.Bursts.Single().Count == 40,
      "particle system settings survive save and load");

// ---- 9. OBJ loader: quads, shared corners, materials from the .mtl, negative indices ----------
File.WriteAllText(Path.Combine(dir, "quad.mtl"), "newmtl Green\nKd 0 1 0\nNs 640\n");
File.WriteAllText(Path.Combine(dir, "quad.obj"), """
    mtllib quad.mtl
    o Quad
    v 0 0 0
    v 1 0 0
    v 1 1 0
    v 0 1 0
    vt 0 0
    vt 1 1
    usemtl Green
    f 1/1 2/1 3/2 4/2
    o Tri
    f -4 -3 -2
    """);
var obj = ModelLoader.Load(Path.Combine(dir, "quad.obj"));
var quad = obj.Meshes.Single(m => m.Name == "Quad");
Check(obj.Meshes.Count == 2 && quad.Indices.Length == 6 && quad.Positions.Length == 12, "OBJ quad becomes two triangles sharing 4 vertices");
Check(quad.Material.BaseColor[1] == 1 && quad.Material.BaseColor[0] == 0 && MathF.Abs(quad.Material.Roughness - 0.2f) < 1e-4f,
      "OBJ material color and roughness come from the .mtl");
Check(obj.Meshes.Single(m => m.Name == "Tri").Material.BaseColor[1] == 1 && obj.Meshes.Single(m => m.Name == "Tri").Material.BaseColor[0] == 1,
      "an OBJ object without usemtl gets the default material");
Check(quad.Normals[2] > 0.99f && quad.TexCoords[1] == 1f, "OBJ missing normals are computed; UVs flipped to glTF orientation");
Check(obj.BoundsMin == Vector3.Zero && obj.BoundsMax == new Vector3(1, 1, 0), "OBJ bounds");

// ---- 10. FBX loader on a generated binary FBX (a quad on a moved, red model) ----------------------
byte[] MakeFbx()
{
    var ms = new MemoryStream();
    var w = new BinaryWriter(ms);
    w.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0"));
    w.Write((byte)0x1A); w.Write((byte)0); w.Write(7400u);

    void Node(string name, object[] props, Action? children = null)
    {
        long start = ms.Position;
        w.Write(0L); w.Write(0u); w.Write((byte)name.Length); w.Write(Encoding.ASCII.GetBytes(name));
        long propStart = ms.Position;
        foreach (var p in props)
            switch (p)
            {
                case long l: w.Write((byte)'L'); w.Write(l); break;
                case int i: w.Write((byte)'I'); w.Write(i); break;
                case double d: w.Write((byte)'D'); w.Write(d); break;
                case string s: w.Write((byte)'S'); w.Write(Encoding.UTF8.GetByteCount(s)); w.Write(Encoding.UTF8.GetBytes(s)); break;
                case int[] ints:
                    w.Write((byte)'i'); w.Write(ints.Length); w.Write(0); w.Write(ints.Length * 4);
                    foreach (var i in ints) w.Write(i);
                    break;
                case double[] doubles:   // zlib-compressed, as exporters write large arrays
                    var raw = new MemoryStream();
                    using (var z = new System.IO.Compression.ZLibStream(raw, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                        foreach (var d in doubles) z.Write(BitConverter.GetBytes(d));
                    w.Write((byte)'d'); w.Write(doubles.Length); w.Write(1); w.Write((int)raw.Length); w.Write(raw.ToArray());
                    break;
            }
        long propEnd = ms.Position;
        if (children is not null) { children(); w.Write(new byte[13]); }
        long end = ms.Position;
        ms.Position = start;
        w.Write((uint)end); w.Write((uint)props.Length); w.Write((uint)(propEnd - propStart));
        ms.Position = end;
    }
    void P(string name, params object[] values) => Node("P", [name, name, "", "A", .. values]);

    Node("GlobalSettings", [], () => Node("Properties70", [], () => P("UnitScaleFactor", 100.0)));   // meters
    Node("Objects", [], () =>
    {
        Node("Geometry", [10L, "Quad\0\u0001Geometry", "Mesh"], () =>
        {
            Node("Vertices", [new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }]);
            Node("PolygonVertexIndex", [new[] { 0, 1, 2, ~3 }]);
        });
        Node("Model", [20L, "Quad\0\u0001Model", "Mesh"], () =>
            Node("Properties70", [], () => P("Lcl Translation", 5.0, 0.0, 0.0)));
        Node("Material", [30L, "Red\0\u0001Material", ""], () =>
            Node("Properties70", [], () => P("DiffuseColor", 1.0, 0.0, 0.0)));
    });
    Node("Connections", [], () =>
    {
        Node("C", ["OO", 10L, 20L]);
        Node("C", ["OO", 30L, 20L]);
        Node("C", ["OO", 20L, 0L]);
    });
    w.Write(new byte[13]);
    return ms.ToArray();
}

var fbxPath = Path.Combine(dir, "quad.fbx");
File.WriteAllBytes(fbxPath, MakeFbx());
var fbx = ModelLoader.Load(fbxPath);
var fbxMesh = fbx.Meshes.Single();
Check(fbxMesh.Name == "Quad" && fbxMesh.Indices.Length == 6 && fbxMesh.Positions.Length == 12, "FBX quad becomes two triangles");
Check(fbx.BoundsMin == new Vector3(5, 0, 0) && fbx.BoundsMax == new Vector3(6, 1, 0), "FBX model translation is applied");
Check(fbxMesh.Material.BaseColor[0] == 1 && fbxMesh.Material.BaseColor[1] == 0, "FBX material color");
Check(fbxMesh.Normals[2] > 0.99f, "FBX missing normals are computed facing the winding");
File.WriteAllText(Path.Combine(dir, "ascii.fbx"), "; FBX 7.4.0 project file\n");
try { ModelLoader.Load(Path.Combine(dir, "ascii.fbx")); Check(false, "ASCII FBX is rejected"); }
catch (InvalidDataException ex) { Check(ex.Message.Contains("ASCII"), "ASCII FBX is rejected with a clear message"); }

// ---- 11. Raycasts: boxes, spheres, meshes, transforms, ignore, order ------------------------------------
{
    var rcScene = new Scene();
    SceneObject RcBox(string name, Vector3 at, Vector3 size, Vector3? scale = null, Quaternion? rotation = null)
    {
        var o = new SceneObject { Name = name, Position = at, Scale = scale ?? Vector3.One, Rotation = rotation ?? Quaternion.Identity };
        o.Components.Add(new BoxCollider { Size = size });
        rcScene.Add(o);
        return o;
    }
    var near = RcBox("Near", new(0, 0, -5), new(2, 2, 2));
    var far = RcBox("Far", new(0, 0, -10), new(2, 2, 2));
    var ball = new SceneObject { Name = "Ball", Position = new(5, 0, 0) };
    ball.Components.Add(new SphereCollider { Radius = 1f });
    rcScene.Add(ball);
    SceneUpdate.Update(rcScene, 1f / 60f);   // makes it the scene scripts query

    bool hitNear = Physics.Raycast(Vector3.Zero, -Vector3.UnitZ, out var nearHit);
    Check(hitNear && nearHit.Object == near && MathF.Abs(nearHit.Distance - 4f) < 1e-4f && nearHit.Normal == Vector3.UnitZ &&
          Vector3.Distance(nearHit.Point, new(0, 0, -4)) < 1e-4f, "raycast hits the nearest box with distance, point and normal");
    bool hitBall = Physics.Raycast(Vector3.Zero, Vector3.UnitX, out var ballHit);
    Check(hitBall && ballHit.Object == ball && MathF.Abs(ballHit.Distance - 4f) < 1e-4f && Vector3.Distance(ballHit.Normal, -Vector3.UnitX) < 1e-4f,
          "raycast hits a sphere");
    Check(!Physics.Raycast(Vector3.Zero, Vector3.UnitY), "a ray that touches nothing misses");
    Check(!Physics.Raycast(Vector3.Zero, -Vector3.UnitZ, 3f), "a hit beyond maxDistance is a miss");
    Check(Physics.Raycast(Vector3.Zero, new Vector3(0, 0, -7), out var unnormalized) && MathF.Abs(unnormalized.Distance - 4f) < 1e-4f,
          "the direction need not be normalized");
    var all = Physics.RaycastAll(Vector3.Zero, -Vector3.UnitZ);
    Check(all.Length == 2 && all[0].Object == near && all[1].Object == far && MathF.Abs(all[1].Distance - 9f) < 1e-4f,
          "RaycastAll returns every hit, nearest first");
    Check(Physics.Raycast(Vector3.Zero, -Vector3.UnitZ, out var skipNear, ignore: near) && skipNear.Object == far,
          "ignore skips an object");
    Check(!Physics.Raycast(new Vector3(0, 0, -4.5f), -Vector3.UnitZ, out var inside, 0.4f), "a ray that starts inside a box does not hit it");
    var longBox = RcBox("Long", new(0, 20, 0), Vector3.One, scale: new(4, 1, 1));
    Check(Physics.Raycast(new Vector3(-10, 20, 0), Vector3.UnitX, out var longHit) && MathF.Abs(longHit.Distance - 8f) < 1e-4f,
          "a scaled box is hit at its scaled size");
    var turned = RcBox("Turned", new(0, 40, 0), Vector3.One, scale: new(4, 1, 1), rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
    Check(Physics.Raycast(new Vector3(-10, 40, 0), Vector3.UnitX, out var turnedHit) && MathF.Abs(turnedHit.Distance - 9.5f) < 1e-3f &&
          Vector3.Distance(turnedHit.Normal, -Vector3.UnitX) < 1e-4f, "a rotated, scaled box is hit through its turned faces");

    // A mesh collider uses the real triangles, from both sides, and the normal faces the ray.
    Engine.Core.Physics.PhysicsWorld.ModelLoader = _ => model;   // the triangle from the GLB test: (0,0,0) (1,0,0) (0,1,0)
    var triObj = new SceneObject { Name = "Tri", Position = new(0, 60, 0) };
    triObj.Components.Add(new MeshCollider { Model = "tri.glb" });
    rcScene.Add(triObj);
    Check(Physics.Raycast(new Vector3(0.2f, 60.2f, 5), -Vector3.UnitZ, out var triHit) && triHit.Object == triObj &&
          MathF.Abs(triHit.Distance - 5f) < 1e-4f && Vector3.Distance(triHit.Normal, Vector3.UnitZ) < 1e-4f, "raycast hits a mesh collider triangle");
    Check(Physics.Raycast(new Vector3(0.2f, 60.2f, -5), Vector3.UnitZ, out var backHit) && Vector3.Distance(backHit.Normal, -Vector3.UnitZ) < 1e-4f,
          "a mesh is hit from behind too, with the normal facing the ray");
    Check(!Physics.Raycast(new Vector3(0.9f, 60.9f, 5), -Vector3.UnitZ), "a ray past the triangle's edge misses");
    Check(Physics.Gravity.Y < 0f, "Physics.Gravity reads the scene's gravity");
}

// ---- 12. Audio: decoding, mixing, panning, distance, doppler, pausing ----------------------------------
byte[] MakeWav(int rate, int channels, int bits, Func<int, int, double> sample, int frames)
{
    var ms = new MemoryStream();
    var w = new BinaryWriter(ms);
    bool isFloat = bits == 32;
    int dataLength = frames * channels * bits / 8;
    w.Write("RIFF"u8); w.Write(36 + dataLength); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)(isFloat ? 3 : 1)); w.Write((short)channels); w.Write(rate);
    w.Write(rate * channels * bits / 8); w.Write((short)(channels * bits / 8)); w.Write((short)bits);
    w.Write("data"u8); w.Write(dataLength);
    for (int f = 0; f < frames; f++)
        for (int c = 0; c < channels; c++)
        {
            double v = sample(f, c);
            if (bits == 8) w.Write((byte)(128 + v * 127));
            else if (bits == 16) w.Write((short)(v * 32767));
            else w.Write((float)v);
        }
    return ms.ToArray();
}

{
    var wav16 = AudioClip.LoadWav(MakeWav(8000, 1, 16, (f, c) => 0.5, 800), "a.wav");
    Check(wav16.SampleRate == 8000 && wav16.Channels == 1 && wav16.FrameCount == 800 && MathF.Abs(wav16.Length - 0.1f) < 1e-6f &&
          Math.Abs(wav16.Samples[10] - 16383) <= 1, "16-bit mono WAV decodes");
    var wavStereo = AudioClip.LoadWav(MakeWav(22050, 2, 16, (f, c) => c == 0 ? 0.25 : -0.25, 100), "b.wav");
    Check(wavStereo.Channels == 2 && wavStereo.FrameCount == 100 && wavStereo.Samples[0] > 0 && wavStereo.Samples[1] < 0, "stereo WAV keeps both channels");
    var wavFloat = AudioClip.LoadWav(MakeWav(8000, 1, 32, (f, c) => -0.5, 50), "c.wav");
    Check(Math.Abs(wavFloat.Samples[0] + 16384) <= 1, "32-bit float WAV decodes");
    var wav8 = AudioClip.LoadWav(MakeWav(8000, 1, 8, (f, c) => 0.0, 50), "d.wav");
    Check(Math.Abs((int)wav8.Samples[0]) <= 256, "8-bit WAV decodes");
    try { AudioClip.LoadWav([1, 2, 3], "junk.wav"); Check(false, "a non-WAV file is rejected"); }
    catch (InvalidDataException ex) { Check(ex.Message.Contains("junk.wav"), "a non-WAV file is rejected, naming the file"); }

    // A stream of empty MPEG-1 layer 3 frames (128 kbps, 44.1 kHz, stereo) decodes to silence.
    var mp3Bytes = new List<byte>();
    for (int i = 0; i < 20; i++) { mp3Bytes.AddRange([0xFF, 0xFB, 0x90, 0x00]); mp3Bytes.AddRange(new byte[413]); }
    var mp3Path = Path.Combine(dir, "silence.mp3");
    File.WriteAllBytes(mp3Path, mp3Bytes.ToArray());
    var mp3 = AudioClip.Load(mp3Path);
    Check(mp3.SampleRate == 44100 && mp3.Channels == 2 && mp3.FrameCount >= 1152 * 15 && mp3.Samples.All(s => s == 0), "mp3 decodes to its sample rate and channels");
    Check(AudioClip.IsAudio("x.MP3") && AudioClip.IsAudio("y.wav") && !AudioClip.IsAudio("z.glb"), "audio files are recognized by extension");
}

{
    var audioDir = Path.Combine(dir, "Audio");
    Directory.CreateDirectory(audioDir);
    File.WriteAllBytes(Path.Combine(audioDir, "half.wav"), MakeWav(8000, 1, 16, (f, c) => 0.5, 8000));    // one second at 0.5
    File.WriteAllBytes(Path.Combine(audioDir, "blip.wav"), MakeWav(8000, 1, 16, (f, c) => 0.5, 800));     // 0.1 s
    AudioEngine.ClipLoader = p => AudioClip.Load(Path.Combine(dir, p));
    AudioEngine.ForgetClips();
    AudioEngine.SampleRate = 8000;
    AudioListener.Volume = 1f;
    AudioListener.Pause = false;

    var buffer = new float[2048 * 2];
    float[] MixBlock(int frames = 256)
    {
        AudioEngine.Mix(buffer, frames);
        return buffer[..(frames * 2)];
    }
    // After the first block the gains have ramped to their targets, so the second block is the steady level.
    (float Left, float Right) Level()
    {
        MixBlock();
        var b = MixBlock();
        return (b[^2], b[^1]);
    }
    bool Near(float a, float b, float tolerance = 0.002f) => MathF.Abs(a - b) <= tolerance;

    var auScene = new Scene();
    var listenerObj = new SceneObject { Name = "Cam" };
    listenerObj.Components.Add(new CameraComponent());
    auScene.Add(listenerObj);
    var auObj = new SceneObject { Name = "Speaker" };
    var src = new AudioSource { Clip = "Audio/half.wav", Loop = true, Volume = 0.5f };
    auObj.Components.Add(src);
    auScene.Add(auObj);
    AudioEngine.Reset(auScene);

    Check(!src.IsPlaying, "an audio source is quiet before the scene updates");
    SceneUpdate.Update(auScene, 1f / 60f);
    Check(src.IsPlaying && AudioEngine.ActiveVoices == 1, "Play On Awake starts the clip on the first update");
    var level = Level();
    Check(Near(level.Left, 0.25f) && Near(level.Right, 0.25f), "a 2D sound plays at clip level times volume, centered");

    src.StereoPan = 1f; SceneUpdate.Update(auScene, 1f / 60f);
    level = Level();
    Check(Near(level.Left, 0f) && Near(level.Right, 0.25f), "stereo pan to the right silences the left channel");
    src.StereoPan = 0f; src.Mute = true; SceneUpdate.Update(auScene, 1f / 60f);
    level = Level();
    Check(Near(level.Left, 0f) && Near(level.Right, 0f), "mute silences the source");
    src.Mute = false; SceneUpdate.Update(auScene, 1f / 60f);
    AudioListener.Volume = 0.5f;
    level = Level();
    Check(Near(level.Left, 0.125f), "the listener's master volume scales everything");
    AudioListener.Volume = 1f;

    // 3D: the source straight ahead of a camera at the origin, three units away.
    src.SpatialBlend = 1f; src.MinDistance = 1f; src.MaxDistance = 5f;
    auObj.Position = new(0, 0, -3);
    SceneUpdate.Update(auScene, 1f / 60f);
    level = Level();
    Check(Near(level.Left, 0.5f * 0.5f / 3f) && Near(level.Right, level.Left), "logarithmic rolloff falls as 1 / distance, centered when straight ahead");
    src.RolloffMode = AudioRolloffMode.Linear; SceneUpdate.Update(auScene, 1f / 60f);
    level = Level();
    Check(Near(level.Left, 0.5f * 0.5f * 0.5f), "linear rolloff falls from MinDistance to MaxDistance");
    auObj.Position = new(1, 0, -0.1f); src.MinDistance = 100f; src.MaxDistance = 200f; SceneUpdate.Update(auScene, 1f / 60f);
    level = Level();
    Check(level.Right > level.Left * 3f, "a source to the right is heard on the right");
    src.Spread = 180f; SceneUpdate.Update(auScene, 1f / 60f);
    level = Level();
    Check(Near(level.Left, level.Right), "spread of 180 centers the sound");
    src.SpatialBlend = 0f; src.Spread = 0f;
    auObj.Position = Vector3.Zero;
    src.RolloffMode = AudioRolloffMode.Logarithmic;

    // Pitch changes how fast the clip is read.
    SceneUpdate.Update(auScene, 1f / 60f);
    src.Pitch = 2f; SceneUpdate.Update(auScene, 1f / 60f);
    src.Time = 0f;
    MixBlock(400);
    Check(Near(src.Time, 0.1f, 0.005f), "a pitch of 2 reads the clip twice as fast");
    src.Pitch = 1f; SceneUpdate.Update(auScene, 1f / 60f);

    // Pause holds the position; UnPause carries on.
    src.Time = 0.2f;
    src.Pause();
    MixBlock(); MixBlock();
    float held = src.Time;
    MixBlock(400);
    Check(!src.IsPlaying && Near(src.Time, held, 0.0001f), "a paused source stays where it is");
    src.UnPause();
    MixBlock(400);
    Check(src.IsPlaying && src.Time > held + 0.04f, "unpausing carries on");

    // Stop, then a delayed start.
    src.Stop();
    MixBlock();
    Check(!src.IsPlaying, "stopping ends the sound");
    src.PlayDelayed(0.05f);   // 400 frames
    var early = MixBlock(300);
    var late = MixBlock(300);
    Check(early.All(v => v == 0f) && late.Skip(200).Any(v => v != 0f), "PlayDelayed waits before sounding");
    src.Stop();
    MixBlock();

    // A sound that is not looped ends and frees its voice.
    src.Clip = "Audio/blip.wav"; src.Loop = false;
    src.Play();
    MixBlock(1024);
    SceneUpdate.Update(auScene, 1f / 60f);
    Check(!src.IsPlaying && AudioEngine.ActiveVoices == 0, "a sound that reaches its end stops and frees its voice");

    // One-shots layer over the source; a point sound needs no object.
    src.Loop = true; src.Clip = "Audio/half.wav"; src.Play();
    src.PlayOneShot("Audio/blip.wav", 0.5f);
    AudioSource.PlayClipAtPoint("Audio/blip.wav", new(0, 0, -2));
    Check(AudioEngine.ActiveVoices == 3, "PlayOneShot and PlayClipAtPoint add voices");
    MixBlock(1024); MixBlock(1024);
    Check(AudioEngine.ActiveVoices == 1, "one-shots free themselves when they end");

    // A source whose object leaves the scene falls silent on the next update.
    auScene.Remove(auObj);
    SceneUpdate.Update(auScene, 1f / 60f);
    MixBlock();
    Check(AudioEngine.ActiveVoices == 0 && !src.IsPlaying, "removing the object stops its sound");

    // Mixing quiet blocks never produces anything outside -1..1, however many sounds pile up.
    AudioEngine.StopAll();
    for (int i = 0; i < 20; i++) AudioSource.PlayClipAtPoint("Audio/half.wav", Vector3.Zero, 1f);
    var loud = MixBlock(1024);
    Check(loud.All(v => v is >= -1f and <= 1f), "the mix is limited to the legal range");
    AudioEngine.StopAll();

    // Doppler: a source moving toward the listener sounds higher, so its clip is read faster.
    var dopObj = new SceneObject { Name = "Siren", Position = new(0, 0, -300) };
    var siren = new AudioSource { Clip = "Audio/half.wav", Loop = true, SpatialBlend = 1f, MinDistance = 1000f, MaxDistance = 2000f };
    dopObj.Components.Add(siren);
    var dopScene = new Scene();
    var dopCamera = new SceneObject { Name = "Cam2" };
    dopCamera.Components.Add(new CameraComponent());
    dopScene.Add(dopCamera);
    dopScene.Add(dopObj);
    SceneUpdate.Update(dopScene, 1f / 60f);
    for (int i = 0; i < 3; i++) { dopObj.Position += new Vector3(0, 0, 1f); SceneUpdate.Update(dopScene, 1f / 60f); }   // 60 units per second toward the camera
    siren.Time = 0f;
    MixBlock(800);
    float expected = 0.1f * (AudioEngine.SpeedOfSound / (AudioEngine.SpeedOfSound - 60f));
    Check(Near(siren.Time, expected, 0.01f), "doppler raises the pitch of a source moving toward the listener");
    siren.DopplerLevel = 0f;
    dopObj.Position += new Vector3(0, 0, 1f); SceneUpdate.Update(dopScene, 1f / 60f);
    siren.Time = 0f;
    MixBlock(800);
    Check(Near(siren.Time, 0.1f, 0.005f), "a Doppler Level of 0 turns the effect off");
    AudioEngine.StopAll();

    // Components survive saving to a scene file.
    var savedAudio = new SceneObject { Name = "S" };
    savedAudio.Components.Add(new AudioSource { Clip = "Audio/x.mp3", Loop = true, Volume = 0.3f, Pitch = 1.5f, SpatialBlend = 0.8f, RolloffMode = AudioRolloffMode.Linear, MinDistance = 2f, MaxDistance = 40f, Priority = 7, Spread = 90f });
    savedAudio.Components.Add(new AudioListener());
    var audioScene = new Scene();
    audioScene.Add(savedAudio);
    var audioBack = SceneSerializer.FromJson(SceneSerializer.ToJson(audioScene)).All.Single();
    var backSource = audioBack.Components.OfType<AudioSource>().Single();
    Check(backSource.Clip == "Audio/x.mp3" && backSource.Loop && Near(backSource.Volume, 0.3f) && Near(backSource.Pitch, 1.5f) &&
          Near(backSource.SpatialBlend, 0.8f) && backSource.RolloffMode == AudioRolloffMode.Linear && backSource.MinDistance == 2f &&
          backSource.MaxDistance == 40f && backSource.Priority == 7 && backSource.Spread == 90f && backSource.PlayOnAwake &&
          audioBack.Components.OfType<AudioListener>().Any(), "audio components save and load with every setting");
}

// ---- 13. Scene manager: replacing, adding, unloading, streaming ---------------------------------------------
var projectDir = Path.Combine(dir, "proj");
Directory.CreateDirectory(Path.Combine(projectDir, "Scenes"));
{
    Scene MakeScene(string name, params (string Name, bool Script)[] objects)
    {
        var s = new Scene { Name = name };
        foreach (var (objectName, script) in objects)
        {
            var o = new SceneObject { Name = objectName };
            if (script) o.Components.Add(new ScriptComponent { ScriptName = "CounterScript" });
            s.Add(o);
        }
        return s;
    }
    SceneSerializer.Save(MakeScene("A", ("a1", true)), Path.Combine(projectDir, "Scenes", "A.empirescene"));
    SceneSerializer.Save(MakeScene("B", ("b1", false), ("b2", false)), Path.Combine(projectDir, "Scenes", "B.empirescene"));
    SceneSerializer.Save(MakeScene("Extra", ("x1", false), ("x2", true)), Path.Combine(projectDir, "Scenes", "Extra.empirescene"));
    var hall = MakeScene("Hall", ("h1", false));
    hall.All.Single().Components.Add(new BoxCollider());
    Directory.CreateDirectory(Path.Combine(projectDir, "Scenes", "Wing"));
    SceneSerializer.Save(hall, Path.Combine(projectDir, "Scenes", "Wing", "Hall.empirescene"));

    var world = SceneSerializer.Load(Path.Combine(projectDir, "Scenes", "A.empirescene"));
    SceneManager.Begin(world, "A", projectDir);
    string loadErrors = ScriptRuntime.Load(world, typeof(CounterScript).Assembly);
    Check(loadErrors == "" && SceneManager.ActiveScene == "A" && SceneManager.IsLoaded("A") && SceneManager.SceneCount == 1, "a game starts in its first scene");
    string Names() => string.Join(",", world.All.Select(o => o.Name).Order());
    CounterScript? Counter(string objectName) =>
        world.All.First(o => o.Name == objectName).Components.OfType<ScriptComponent>().FirstOrDefault()?.Instance as CounterScript;
    void Frame(int count = 1) { for (int i = 0; i < count; i++) SceneUpdate.Update(world, 1f / 60f); }

    Frame(3);
    Check(Counter("a1")!.count == 3 && Counter("a1")!.startCount == 1, "scripts run: Start once, then Update every frame");

    SceneManager.LoadScene("B");
    Check(Names() == "a1", "loading a scene waits for the next frame, so a running script is not disturbed");
    Frame();
    Check(Names() == "b1,b2" && SceneManager.ActiveScene == "B" && SceneManager.LoadedScenes.SequenceEqual(new[] { "B" }) &&
          world.All.All(o => o.SceneName == "B") && world.Name == "B", "loading a scene replaces the current one");

    SceneManager.LoadScene("Extra", LoadSceneMode.Additive);
    Frame(2);
    Check(Names() == "b1,b2,x1,x2" && SceneManager.IsLoaded("Extra") && SceneManager.SceneCount == 2 && SceneManager.ActiveScene == "B",
          "an additive load adds a scene and keeps the active one");
    Check(world.All.First(o => o.Name == "x1").SceneName == "Extra" && Counter("x2") is { count: >= 1, startCount: 1 },
          "added objects are tagged with their scene, and their scripts start and run");

    SceneManager.LoadScene("Extra", LoadSceneMode.Additive);
    Frame();
    Check(Names() == "b1,b2,x1,x2", "a scene that is already loaded is not added twice");

    var keep = world.All.First(o => o.Name == "b1");
    SceneManager.DontDestroyOnLoad(keep);
    SceneManager.UnloadScene("Extra");
    Frame();
    Check(Names() == "b1,b2" && !SceneManager.IsLoaded("Extra"), "unloading a scene removes only its objects");

    SceneManager.LoadScene("A");
    Frame();
    Check(Names() == "a1,b1" && keep.Persistent && world.Contains(keep), "DontDestroyOnLoad keeps an object through a replacing load");

    try { SceneManager.LoadScene("Nope"); Check(false, "a missing scene is an error"); }
    catch (FileNotFoundException ex) { Check(ex.Message.Contains("Nope"), "a missing scene is an error that names it"); }
    try { SceneManager.UnloadScene("Nope"); Check(false, "unloading a scene that is not loaded is an error"); }
    catch (InvalidOperationException) { Check(true, "unloading a scene that is not loaded is an error"); }

    // Scenes in a subfolder load by path, and a scene read in the background arrives when it is ready.
    int warmed = 0;
    SceneManager.AssetWarmer = _ => Interlocked.Increment(ref warmed);
    var op = SceneManager.LoadSceneAsync("Wing/Hall", LoadSceneMode.Additive);
    for (int i = 0; i < 400 && !op.IsDone; i++) { Frame(); Thread.Sleep(5); }
    Check(op.IsDone && op.Error is null && op.Progress == 1f && SceneManager.IsLoaded("Hall") && Names().Contains("h1"), "an async load arrives, from a subfolder");
    Check(warmed == 1, "an async load lets the host preload the scene's assets");
    Check(Physics.Raycast(new Vector3(0, 5, 0), -Vector3.UnitY, out var hallHit) && hallHit.Object.Name == "h1", "streamed-in colliders take part in physics queries");
    SceneManager.AssetWarmer = null;

    // The scene manager stops sounds from objects it removes.
    var soundScene = new Scene();
    var soundObj = new SceneObject { Name = "Music" };
    soundObj.Components.Add(new AudioSource { Clip = "Audio/half.wav", Loop = true });
    soundScene.Add(soundObj);
    AudioEngine.Reset(soundScene);
    SceneUpdate.Update(soundScene, 1f / 60f);
    Check(AudioEngine.ActiveVoices == 1, "a looping music source plays");
    AudioEngine.StopAll();
}

// ---- 14. Save games: slots, scene state, script fields, custom values, sounds ------------------------------------
{
    SaveGame.Directory = Path.Combine(dir, "saves");
    if (Directory.Exists(SaveGame.Directory)) Directory.Delete(SaveGame.Directory, recursive: true);

    var world = SceneSerializer.Load(Path.Combine(projectDir, "Scenes", "A.empirescene"));
    var hero = new SceneObject { Name = "Hero", Position = new(1, 2, 3) };
    hero.Components.Add(new Rigidbody { UseGravity = false, Velocity = new(0, 5, 0), Drag = 0f });
    world.Add(hero);
    var music = new SceneObject { Name = "Music" };
    var musicSource = new AudioSource { Clip = "Audio/half.wav", Loop = true };
    music.Components.Add(musicSource);
    world.Add(music);

    SceneManager.Begin(world, "A", projectDir);
    ScriptRuntime.Load(world, typeof(CounterScript).Assembly);
    AudioEngine.SampleRate = 8000;
    void Frame(int count = 1) { for (int i = 0; i < count; i++) SceneUpdate.Update(world, 1f / 60f); }
    var counter = () => (CounterScript)world.All.First(o => o.Name == "a1").Components.OfType<ScriptComponent>().Single().Instance!;
    var audioBuffer = new float[1024];

    Frame(5);
    AudioEngine.Mix(audioBuffer, 512);   // 0.064 s into the music
    SaveGame.SetInt("coins", 7);
    SaveGame.SetFloat("health", 0.75f);
    SaveGame.SetString("name", "Ada");
    SaveGame.SetBool("hasKey", true);
    Check(SaveGame.GetInt("coins") == 7 && SaveGame.GetFloat("health") == 0.75f && SaveGame.GetString("name") == "Ada" && SaveGame.GetBool("hasKey") &&
          SaveGame.GetInt("missing", 42) == 42 && SaveGame.HasKey("coins") && !SaveGame.HasKey("missing"), "custom values read back, with defaults for missing keys");

    int savedCount = counter().count;
    Check(SaveGame.Save("slot 1") && SaveGame.Exists("slot 1") && SaveGame.Slots().SequenceEqual(new[] { "slot 1" }), "saving writes a slot");
    Check(SaveGame.Timestamp("slot 1") is { } stamp && (DateTime.Now - stamp).TotalMinutes < 1, "a slot records when it was saved");

    // Change everything, then load the slot.
    hero.Position = new(50, 50, 50);
    Frame(10);
    SaveGame.SetInt("coins", 99);
    SaveGame.DeleteKey("name");
    SceneManager.LoadScene("B");
    Frame();
    Check(world.All.All(o => o.Name != "Hero") && SceneManager.ActiveScene == "B", "the game really moved on before loading");

    Check(SaveGame.Load("slot 1"), "loading a slot that exists succeeds");
    Check(world.All.All(o => o.Name != "Hero"), "a loaded game takes effect at the start of the next frame, not at once");
    Frame();
    var restoredHero = world.All.First(o => o.Name == "Hero");
    var restoredBody = restoredHero.Components.OfType<Rigidbody>().Single();
    Check(SceneManager.ActiveScene == "A" && SceneManager.LoadedScenes.SequenceEqual(new[] { "A" }) && world.All.Any(o => o.Name == "a1") &&
          !world.All.Any(o => o.Name == "b1"), "the saved scenes come back");
    Check(restoredHero.Position.X == 1f && restoredHero.Position.Z == 3f && restoredHero.Position.Y is > 2.3f and < 2.7f, "object positions are restored (the rigidbody keeps moving from there)");
    Check(restoredBody.Velocity.Y is > 4.9f and < 5.1f, "a rigidbody keeps its speed");
    var restored = counter();
    Check(restored.count == savedCount + 1 && restored.startCount == 1, "a script's public fields are restored, and Start does not run again");
    Check(restored.loadedCalls == 1, "OnGameLoaded runs on restored scripts");
    Check(SaveGame.GetInt("coins") == 7 && SaveGame.GetString("name") == "Ada" && SaveGame.GetFloat("health") == 0.75f && SaveGame.GetBool("hasKey"),
          "custom values are restored with the game");
    var restoredMusic = world.All.First(o => o.Name == "Music").Components.OfType<AudioSource>().Single();
    Check(restoredMusic.IsPlaying && restoredMusic.Time is > 0.05f and < 0.2f, "a sound that was playing carries on from where it was");

    // Slots.
    Check(SaveGame.Save("slot2") && SaveGame.Slots().SequenceEqual(new[] { "slot 1", "slot2" }), "slots list in order");
    SaveGame.Delete("slot2");
    Check(!SaveGame.Exists("slot2") && SaveGame.Timestamp("slot2") is null, "deleting a slot removes it");
    Check(!SaveGame.Load("slot2"), "loading an empty slot fails without changing the game");
    Check(!SaveGame.Save("bad/name") && !SaveGame.Load("../escape"), "slot names cannot be paths");
    File.WriteAllText(Path.Combine(SaveGame.Directory, "broken.empiresave"), "{ not json");
    Check(!SaveGame.Load("broken"), "a damaged save fails without changing the game");
    Frame();
    Check(world.All.Any(o => o.Name == "Hero"), "the game carries on after a failed load");
    AudioEngine.StopAll();
}

// ---- Animation: skinned rigMesh, rigid node, clips ----------------------------------------------------------
{
    var bin = new MemoryStream();
    var bw = new BinaryWriter(bin);
    var views = new List<string>();
    var accessors = new List<string>();
    int Add(Action<BinaryWriter> write, string componentType, int count, string type, string extra = "")
    {
        while (bin.Position % 4 != 0) bw.Write((byte)0);
        long start = bin.Position;
        write(bw);
        views.Add($"{{ \"buffer\": 0, \"byteOffset\": {start}, \"byteLength\": {bin.Position - start} }}");
        accessors.Add($"{{ \"bufferView\": {views.Count - 1}, \"componentType\": {componentType}, \"count\": {count}, \"type\": \"{type}\" {extra} }}");
        return accessors.Count - 1;
    }
    void Floats(BinaryWriter w, params float[] v) { foreach (var f in v) w.Write(f); }

    int pos = Add(w => Floats(w, 0, 0, 0, 1, 0, 0, 0, 2, 0, 1, 2, 0), "5126", 4, "VEC3", ", \"min\": [0,0,0], \"max\": [1,2,0]");
    int joints = Add(w => { foreach (byte b in new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }) w.Write(b); }, "5121", 4, "VEC4");
    int weights = Add(w => Floats(w, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0), "5126", 4, "VEC4");
    int idx = Add(w => { foreach (ushort i in new ushort[] { 0, 1, 2, 1, 3, 2 }) w.Write(i); }, "5123", 6, "SCALAR");
    int ibm = Add(w => Floats(w, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
                                 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1, 0, 1), "5126", 2, "MAT4");
    int bendTimes = Add(w => Floats(w, 0, 1), "5126", 2, "SCALAR", ", \"min\": [0], \"max\": [1]");
    float h = MathF.Sqrt(0.5f);   // a quarter turn about Z: (0, 0, sin 45, cos 45)
    int bendRot = Add(w => Floats(w, 0, 0, 0, 1, 0, 0, h, h), "5126", 2, "VEC4");
    int liftTimes = Add(w => Floats(w, 0, 2), "5126", 2, "SCALAR", ", \"min\": [0], \"max\": [2]");
    int liftOut = Add(w => Floats(w, 0, 0, 0, 0, 5, 0), "5126", 2, "VEC3");
    int boxPos = Add(w => Floats(w, 0, 0, 0, 1, 0, 0, 0, 1, 0), "5126", 3, "VEC3", ", \"min\": [0,0,0], \"max\": [1,1,0]");
    int boxIdx = Add(w => { foreach (ushort i in new ushort[] { 0, 1, 2 }) w.Write(i); w.Write((ushort)0); }, "5123", 3, "SCALAR");
    byte[] binBytes = bin.ToArray();

    string json = $$"""
    {
      "asset": { "version": "2.0" },
      "buffers": [ { "byteLength": {{binBytes.Length}} } ],
      "bufferViews": [ {{string.Join(",", views)}} ],
      "accessors": [ {{string.Join(",", accessors)}} ],
      "nodes": [
        { "name": "Root", "children": [1] },
        { "name": "Bone", "translation": [0, 1, 0] },
        { "name": "Sleeve", "mesh": 0, "skin": 0 },
        { "name": "Box", "mesh": 1, "translation": [3, 0, 0] }
      ],
      "skins": [ { "joints": [0, 1], "inverseBindMatrices": {{ibm}} } ],
      "meshes": [
        { "name": "Sleeve", "primitives": [ { "attributes": { "POSITION": {{pos}}, "JOINTS_0": {{joints}}, "WEIGHTS_0": {{weights}} }, "indices": {{idx}} } ] },
        { "name": "Box", "primitives": [ { "attributes": { "POSITION": {{boxPos}} }, "indices": {{boxIdx}} } ] }
      ],
      "animations": [
        { "name": "Bend", "samplers": [ { "input": {{bendTimes}}, "output": {{bendRot}} } ], "channels": [ { "sampler": 0, "target": { "node": 1, "path": "rotation" } } ] },
        { "samplers": [ { "input": {{liftTimes}}, "output": {{liftOut}}, "interpolation": "STEP" } ], "channels": [ { "sampler": 0, "target": { "node": 3, "path": "translation" } } ] }
      ]
    }
    """;
    var jsonBytes = Encoding.UTF8.GetBytes(json);
    int jsonPad = (4 - jsonBytes.Length % 4) % 4;
    var glb = new MemoryStream();
    var gw = new BinaryWriter(glb);
    gw.Write(0x46546C67u); gw.Write(2u);
    gw.Write((uint)(12 + 8 + jsonBytes.Length + jsonPad + 8 + binBytes.Length));
    gw.Write((uint)(jsonBytes.Length + jsonPad)); gw.Write(0x4E4F534Au); gw.Write(jsonBytes); for (int i = 0; i < jsonPad; i++) gw.Write((byte)0x20);
    gw.Write((uint)binBytes.Length); gw.Write(0x004E4942u); gw.Write(binBytes);
    string animPath = Path.Combine(dir, "rig.glb");
    File.WriteAllBytes(animPath, glb.ToArray());

    var names = ModelLoader.AnimationNames(animPath);
    Check(names.SequenceEqual(new[] { "Bend", "Animation 2" }), "animation names are read from the file's JSON (unnamed ones get a number)");
    Check(ModelLoader.AnimationNames(Path.Combine(dir, "tri.glb")).Count == 0 || !File.Exists(Path.Combine(dir, "tri.glb")), "a model without animations lists none");

    var rigModel = ModelLoader.Load(animPath);
    Check(rigModel.Animations.Count == 2 && rigModel.Nodes.Count == 4 && rigModel.Skins.Count == 1, "nodes, skins and animations are loaded");
    Check(rigModel.Meshes.Count == 2 && rigModel.Meshes[0].Skin == 0 && rigModel.Meshes[1].Skin == -1, "a skinned and a rigid mesh load");
    Check(rigModel.Meshes[1].RestTransform.Translation == new Vector3(3, 0, 0), "a rigid mesh sits at its node at rest");

    // Skinning: a quarter turn of the bone at y = 1 swings the top of the sleeve from (0, 2) to (-1, 1).
    var scene2 = new Scene();
    var actor = new SceneObject { Name = "Actor" };
    actor.Components.Add(new MeshRenderer { Model = "rig.glb" });
    var animator = new Animator();
    animator.Animations.Add(new AnimationEntry { Id = "bend", Model = "rig.glb", Clip = "Bend", Loop = false });
    animator.Animations.Add(new AnimationEntry { Id = "lift", Model = "rig.glb", Clip = "Animation 2", Loop = true, Speed = 2f });
    actor.Components.Add(animator);
    scene2.Add(actor);
    Animator.ModelLoader = _ => rigModel;

    var rigMesh = actor.Components.OfType<MeshRenderer>().Single();
    Animators.Update(scene2, 0f);   // the first update tells the animator which object it is on (scripts and the editor do that too)
    animator.Play("bend");
    Animators.Update(scene2, 0f);
    var pose0 = rigMesh.Pose;
    Check(pose0 is not null && animator.IsPlaying("bend") && animator.CurrentId == "bend", "playing by id starts the animation");
    Check(pose0!.Positions[0] is { } p0 && MathF.Abs(p0[7] - 2f) < 1e-4f && MathF.Abs(p0[6]) < 1e-4f, "at time 0 the skinned vertex is where the mesh says");
    Animators.Update(scene2, 1f);
    var bent = rigMesh.Pose!.Positions[0]!;
    Check(MathF.Abs(bent[6] + 1f) < 1e-4f && MathF.Abs(bent[7] - 1f) < 1e-4f, "after the clip the bent vertex has swung to (-1, 1)");
    Check(MathF.Abs(bent[0]) < 1e-4f && MathF.Abs(bent[1]) < 1e-4f, "vertices on the fixed joint do not move");
    Check(!animator.IsPlaying("bend") && animator.CurrentId == "", "a clip that doesn't loop finishes");
    Check(MathF.Abs(rigMesh.Pose!.Positions[0]![6] + 1f) < 1e-4f, "a finished clip holds its last pose");

    animator.Play("lift");
    Animators.Update(scene2, 0.25f);   // 0.5 s into a looping clip played at double speed
    Check(rigMesh.Pose!.MeshTransforms[1].Translation == Vector3.Zero, "a STEP channel holds its first key until the next (the animated position replaces the node's own)");
    Animators.Update(scene2, 1.2f);    // wraps at 2 s: 0.5 + 2.4 = 2.9 -> 0.9
    Check(animator.IsPlaying("lift") && animator.Time is > 0.85f and < 0.95f, "a looping clip wraps around");

    animator.Play("bend", fadeSeconds: 1f);
    Animators.Update(scene2, 0.5f);
    Check(animator.IsPlaying("bend"), "a fade-in plays the new animation");
    animator.Stop();
    Check(rigMesh.Pose is null && !animator.IsPlaying(), "Stop puts the model back at rest");

    bool threw = false;
    try { animator.Play("nope"); } catch (InvalidOperationException ex) { threw = ex.Message.Contains("'bend'"); }
    Check(threw, "playing an unknown id says which ids exist");

    // PlayAtStart, and saving and loading the animator.
    animator.Animations[1].PlayAtStart = true;
    var json2 = SceneSerializer.ToJson(scene2);
    var again = SceneSerializer.FromJson(json2);
    var a2 = again.All.Single().Components.OfType<Animator>().Single();
    Check(a2.Animations.Count == 2 && a2.Animations[1].Id == "lift" && a2.Animations[1].Clip == "Animation 2" &&
          a2.Animations[1].PlayAtStart && a2.Animations[1].Loop && a2.Animations[1].Speed == 2f && a2.Animations[0].Model == "rig.glb",
          "an animator and its animations survive saving");
    again.All.Single().Components.OfType<MeshRenderer>().Single();
    Animators.Update(again, 0.1f);
    Check(a2.IsPlaying("lift"), "an animation set to play at start starts by itself");
    Animators.Reset(again);
    Check(!a2.IsPlaying(), "resetting stops everything");
}

// ---- Terrain: heights, brush, saving, mesh data, physics -------------------------------------------------
{
    var terrain = new Terrain { Width = 40f, Length = 40f, Height = 10f };
    terrain.Generate(seed: 7);
    var again = new Terrain { Width = 40f, Length = 40f, Height = 10f };
    again.Generate(seed: 7);
    Check(terrain.HeightsData == again.HeightsData, "the same seed makes the same hills");
    again.Generate(seed: 8);
    Check(terrain.HeightsData != again.HeightsData, "another seed makes other hills");

    float lo = float.MaxValue, hi = float.MinValue;
    for (float x = -20f; x <= 20f; x += 1.3f)
        for (float z = -20f; z <= 20f; z += 1.3f) { float h = terrain.HeightAt(x, z); lo = MathF.Min(lo, h); hi = MathF.Max(hi, h); }
    Check(lo >= 0f && hi <= 10f && hi - lo > 2f, "generated heights stay within 0 and Height and vary");

    Check(terrain.Raycast(new Vector3(3f, 50f, -4f), -Vector3.UnitY, 100f, out float rayT) && MathF.Abs(50f - rayT - terrain.HeightAt(3f, -4f)) < 0.01f,
          "a ray down from above meets the ground at its height");
    Check(!terrain.Raycast(new Vector3(100f, 50f, 0f), -Vector3.UnitY, 100f, out _), "a ray beside the terrain misses");

    var flat = new Terrain { Width = 40f, Length = 40f, Height = 10f };
    flat.Flatten(2f);
    Check(MathF.Abs(flat.HeightAt(5f, 5f) - 2f) < 0.01f, "Flatten sets one height everywhere");
    flat.Sculpt(TerrainBrush.Raise, 0f, 0f, 5f, 3f);
    Check(MathF.Abs(flat.HeightAt(0f, 0f) - 5f) < 0.05f, "Raise lifts the ground at the brush's center by the strength");
    Check(flat.HeightAt(0f, 3f) > 2.2f && flat.HeightAt(0f, 3f) < 4.9f, "the brush fades toward its edge");
    Check(MathF.Abs(flat.HeightAt(15f, 15f) - 2f) < 0.01f, "the ground outside the brush is untouched");
    flat.Sculpt(TerrainBrush.Lower, 0f, 0f, 5f, 3f);
    Check(MathF.Abs(flat.HeightAt(0f, 0f) - 2f) < 0.05f, "Lower digs it back down");
    int version = flat.Version;
    flat.Sculpt(TerrainBrush.Raise, 100f, 100f, 2f, 1f);
    Check(flat.Version == version, "a brush that touches nothing changes nothing");
    flat.Sculpt(TerrainBrush.Raise, 0f, 0f, 5f, 8f);
    float bump = flat.HeightAt(0f, 0f);
    flat.Sculpt(TerrainBrush.Smooth, 0f, 0f, 5f, 1f);
    Check(flat.HeightAt(0f, 0f) < bump, "Smooth lowers a spike toward its surroundings");
    flat.Sculpt(TerrainBrush.Flatten, 0f, 0f, 8f, 1f, level: 4f);
    Check(MathF.Abs(flat.HeightAt(0f, 0f) - 4f) < 0.05f, "Flatten pulls the ground to the chosen height");

    int resolutionBefore = terrain.Resolution;
    float before = terrain.HeightAt(4f, 4f);
    terrain.Resolution = 257;
    Check(resolutionBefore == 129 && terrain.Resolution == 257 && MathF.Abs(terrain.HeightAt(4f, 4f) - before) < 0.1f, "resizing keeps the shape of the ground");
    terrain.Resolution = 65;

    // Saving
    var sceneT = new Scene();
    var holder = new SceneObject { Name = "Hills", Position = new(5, 1, 0) };
    terrain.BaseColor = new Vector3(0.1f, 0.2f, 0.3f);
    terrain.Collide = false;
    terrain.Friction = 0.9f;
    holder.Components.Add(terrain);
    sceneT.Add(holder);
    var restored = SceneSerializer.FromJson(SceneSerializer.ToJson(sceneT)).Find(holder.Id)!.Components.OfType<Terrain>().Single();
    var oldTerrain = SceneSerializer.FromJson("""{ "formatVersion": 1, "name": "Old", "objects": [ { "id": "6f1d2c3a-0000-4000-8000-000000000001", "name": "T", "position": [0,0,0], "rotation": [0,0,0,1], "scale": [1,1,1], "components": [ { "type": "Terrain", "terrainData": { "resolution": 33, "midColor": [0.2, 0.6, 0.1] } } ] } ] }""")
        .All.Single().Components.OfType<Terrain>().Single();
    Check(oldTerrain.BaseColor == new Vector3(0.2f, 0.6f, 0.1f), "a terrain saved with the old automatic colors keeps its grass color as the base");
    Check(restored.Resolution == 65 && restored.Width == 40f && restored.Height == 10f && restored.HeightsData == terrain.HeightsData, "a terrain's size and heights survive saving");
    Check(restored.BaseColor == new Vector3(0.1f, 0.2f, 0.3f) && !restored.Collide && MathF.Abs(restored.Friction - 0.9f) < 1e-6f, "its colors and collision settings survive saving");

    // Mesh data for drawing
    var (meshModel, pose) = terrain.Prepare();
    int key = terrain.StructureKey;
    Check(meshModel.Meshes.Count == 1 && meshModel.Meshes[0].Positions.Length == 65 * 65 * 3 && meshModel.Meshes[0].Indices.Length == 64 * 64 * 6, "the drawn mesh has a vertex per grid point and two triangles per square");
    Check(pose.Positions[0]![1] >= 0f && pose.Normals[0]![1] > 0f && pose.TexCoords![0]!.All(u => u is >= 0f and <= 1f), "vertices have positions, upward normals and palette coordinates");
    terrain.Sculpt(TerrainBrush.Raise, 0f, 0f, 6f, 2f);
    var (meshModel2, _) = terrain.Prepare();
    Check(ReferenceEquals(meshModel, meshModel2) && terrain.StructureKey == key, "sculpting keeps the same mesh and only moves its vertices");
    int colorVersion = terrain.ColorVersion;
    terrain.BaseColor = new Vector3(1f, 0f, 0f);
    var (meshModel3, pose3) = terrain.Prepare();
    Check(ReferenceEquals(meshModel, meshModel3) && terrain.ColorVersion != colorVersion && pose3.Colors![0]!.Length == 65 * 65 * 4,
          "changing the base color recolors the same mesh");
    Check(pose3.Colors![0]!.Where((_, i) => i % 4 == 1).All(g => g == 0f) && pose3.Colors[0]!.Where((_, i) => i % 4 == 0).All(r => r == 1f),
          "unpainted ground is the base color everywhere, whatever its height or slope");

    // Paint layers
    var paint = new Terrain { Width = 40f, Length = 40f, Height = 10f };
    paint.Flatten(2f);
    int dirt = paint.AddLayer("Dirt", new Vector3(1f, 0f, 0f));
    int sand = paint.AddLayer("Sand", new Vector3(0f, 0f, 1f));
    Check(dirt == 0 && sand == 1 && paint.Layers.Count == 2 && paint.Layers[0].Name == "Dirt", "layers can be added");
    var (paintModel, paintPose) = paint.Prepare();
    int centerIndex = (paint.Resolution / 2) * paint.Resolution + paint.Resolution / 2;
    float[] autoColor = paintPose.Colors![0]!.Skip(centerIndex * 4).Take(3).ToArray();
    paint.Sculpt(TerrainBrush.Paint, 0f, 0f, 5f, 1f, layer: dirt);
    var (_, paintPose2) = paint.Prepare();
    float[] painted = paintPose2.Colors![0]!.Skip(centerIndex * 4).Take(3).ToArray();
    Check(painted[0] > 0.99f && painted[1] < 0.01f && painted[2] < 0.01f && autoColor.Sum() > 0.3f, "painting a layer at full strength turns the ground that layer's color");
    Check(ReferenceEquals(paintModel, paintPose2.Model) && paintPose2.Positions[0]![centerIndex * 3 + 1] > 0f, "painting recolors without remaking the mesh");
    int farIndex = 5 * paint.Resolution + 5;
    Check(MathF.Abs(paintPose2.Colors![0]![farIndex * 4] - autoColor[0]) < 0.2f && paintPose2.Colors[0]![farIndex * 4 + 2] > 0.01f, "ground outside the brush keeps its base color");
    Check(paint.LayerAt(0f, 0f) == dirt && paint.LayerAt(15f, 15f) == -1, "LayerAt tells which layer is painted where");
    paint.Sculpt(TerrainBrush.Paint, 0f, 0f, 5f, 1f, layer: sand);
    Check(paint.GetLayerWeight(sand, paint.Resolution / 2, paint.Resolution / 2) > 0.99f && paint.GetLayerWeight(dirt, paint.Resolution / 2, paint.Resolution / 2) < 0.01f,
          "painting a second layer over the first replaces it where it is full strength");
    paint.Sculpt(TerrainBrush.Paint, 0f, 0f, 5f, 0.3f, layer: dirt);
    float total = paint.GetLayerWeight(dirt, paint.Resolution / 2, paint.Resolution / 2) + paint.GetLayerWeight(sand, paint.Resolution / 2, paint.Resolution / 2);
    Check(total > 0.98f && total < 1.02f && paint.GetLayerWeight(dirt, paint.Resolution / 2, paint.Resolution / 2) > 0.2f, "layers share a spot: their strengths add up to one");

    var paintScene = new Scene();
    var paintHolder = new SceneObject { Name = "Painted" };
    paintHolder.Components.Add(paint);
    paintScene.Add(paintHolder);
    var paintBack = SceneSerializer.FromJson(SceneSerializer.ToJson(paintScene)).Find(paintHolder.Id)!.Components.OfType<Terrain>().Single();
    Check(paintBack.Layers.Count == 2 && paintBack.Layers[1].Name == "Sand" && paintBack.Layers[1].Color == new Vector3(0f, 0f, 1f) &&
          paintBack.LayerWeightsData(0) == paint.LayerWeightsData(0) && paintBack.LayerWeightsData(1) == paint.LayerWeightsData(1) && paint.LayerWeightsData(0).Length > 0,
          "layers, their colors and where they are painted survive saving");

    paint.Sculpt(TerrainBrush.Erase, 0f, 0f, 5f, 1f);
    Check(paint.LayerAt(0f, 0f) == -1 && paint.GetLayerWeight(sand, paint.Resolution / 2, paint.Resolution / 2) < 0.01f, "Erase takes the paint off");
    paint.FillLayer(sand);
    Check(paint.LayerAt(15f, 15f) == sand && paint.LayerAt(-15f, 3f) == sand, "Fill covers the whole terrain with a layer");
    paint.Resolution = 33;
    Check(paint.LayerAt(15f, 15f) == sand, "resizing keeps the paint");
    paint.SetLayer(sand, "Beach", new Vector3(0f, 1f, 0f));
    Check(paint.Layers[sand].Name == "Beach" && paint.Prepare().Pose.Colors![0]![1] > 0.99f, "changing a layer's color changes the painted ground");
    paint.RemoveLayer(dirt);
    Check(paint.Layers.Count == 1 && paint.Layers[0].Name == "Beach" && paint.LayerAt(15f, 15f) == 0, "removing a layer keeps the others and their paint");
    paint.ClearPaint();
    Check(paint.LayerAt(15f, 15f) == -1, "Clear Paint removes all paint");
    for (int i = 0; i < 12; i++) paint.AddLayer($"L{i}", Vector3.One);
    Check(paint.Layers.Count == Terrain.MaxLayers, "the number of layers is limited");

    // Physics: a ball dropped on the ground rests on it, and raycasts hit it
    var physics = new Scene();
    var ground = new SceneObject { Name = "Ground", Position = new(0, 0, 0) };
    var gt = new Terrain { Width = 40f, Length = 40f, Height = 10f };
    gt.Generate(seed: 3);
    ground.Components.Add(gt);
    physics.Add(ground);
    var dropBall = new SceneObject { Position = new(2f, 30f, 3f) };
    dropBall.Components.Add(new Rigidbody());
    dropBall.Components.Add(new SphereCollider { Radius = 0.5f });
    physics.Add(dropBall);
    for (int i = 0; i < 60 * 6; i++) physics.Physics.Step(physics, 1f / 60f);
    float expected = gt.HeightAt(dropBall.Position.X, dropBall.Position.Z) + 0.5f;
    Check(dropBall.Position.Y > expected - 0.25f && dropBall.Position.Y < expected + 1.5f && MathF.Abs(dropBall.Position.X) < 20f,
          $"a ball dropped on a terrain lands on it (y {dropBall.Position.Y:F2}, ground {expected - 0.5f:F2})");
    SceneUpdate.Update(physics, 1f / 60f);
    Check(Physics.Raycast(new Vector3(-6f, 60f, 5f), -Vector3.UnitY, out var groundHit, 100f) && groundHit.Collider == gt &&
          MathF.Abs(groundHit.Point.Y - gt.HeightAt(-6f, 5f)) < 0.05f, "a script's raycast hits the terrain at the ground's height");
    gt.Collide = false;
    Check(!Physics.Raycast(new Vector3(-6f, 60f, 5f), -Vector3.UnitY, out _, 100f), "a terrain with Collide off is not hit");
}

// ---- Mouse input and shadow settings ---------------------------------------------------------------------
{
    var mouseScene = new Scene();
    var target = new SceneObject { Name = "Target", Position = new(0, 0, -10) };
    target.Components.Add(new BoxCollider { Size = new(2, 2, 2) });
    mouseScene.Add(target);

    ScriptRuntime.Load(mouseScene, (Assembly?)null);   // a game starting: input begins fresh
    var state = new MouseState(new Vector2(100, 50), false, false, false, 0f);
    Input.MouseProvider = () => state;
    SceneUpdate.Update(mouseScene, 1f / 60f);
    Check(Input.mousePosition == new Vector2(100, 50) && Input.mouseDelta == Vector2.Zero, "the mouse position is read, with no movement on the first frame");

    state = state with { Position = new Vector2(110, 45), Left = true, Scroll = 2f };
    SceneUpdate.Update(mouseScene, 1f / 60f);
    Check(Input.GetMouseButtonDown(0) && Input.GetMouseButton(MouseButton.Left) && Input.GetMouseButton("left") && !Input.GetMouseButton(1),
          "a button press is seen on the frame it happens, by number, name and enum");
    Check(Input.mouseDelta == new Vector2(10, -5) && Input.mouseScrollDelta == 2f, "mouseDelta and mouseScrollDelta report this frame's movement");
    Check(MathF.Abs(Input.GetAxis("Mouse X") - 1f) < 1e-5f && MathF.Abs(Input.GetAxis("Mouse Y") - 0.5f) < 1e-5f, "Mouse X and Mouse Y follow the movement (up is positive)");

    state = state with { Scroll = 0f };
    SceneUpdate.Update(mouseScene, 1f / 60f);
    Check(!Input.GetMouseButtonDown(0) && Input.GetMouseButton(0) && Input.mouseDelta == Vector2.Zero, "a held button is not pressed again on the next frame");
    state = state with { Left = false };
    SceneUpdate.Update(mouseScene, 1f / 60f);
    Check(Input.GetMouseButtonUp(0) && !Input.GetMouseButton(0), "letting go is seen on the frame it happens");

    Input.cursorLocked = true;
    state = state with { Position = new Vector2(900, 900) };
    SceneUpdate.Update(mouseScene, 1f / 60f);
    Check(Input.mouseDelta == Vector2.Zero, "locking the cursor does not count its jump as movement");
    Input.cursorLocked = false;

    bool threw = false;
    try { Input.GetMouseButton(7); } catch (ArgumentException) { threw = true; }
    Check(threw, "an unknown mouse button is a clear error");

    // A camera at the origin looking down -Z: the middle of the view points straight ahead and hits the box.
    var view = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
    var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 2f, 0.1f, 100f);
    Input.SetView(new Vector2(800, 400), view * projection);
    var middle = Input.ScreenPointToRay(new Vector2(400, 200));
    Check(Vector3.Distance(middle.Direction, -Vector3.UnitZ) < 1e-4f && middle.Origin.Length() < 0.2f, "the ray through the middle of the view points where the camera looks");
    var right = Input.ScreenPointToRay(new Vector2(800, 200));
    Check(right.Direction.X > 0.5f && MathF.Abs(right.Direction.Y) < 1e-4f, "the ray through the right edge points to the right");
    state = state with { Position = new Vector2(400, 200) };
    SceneUpdate.Update(mouseScene, 1f / 60f);
    var mouseRay = Input.GetMouseRay();
    bool mouseHitAny = Physics.Raycast(mouseRay, out var mouseHit);
    Check(mouseHitAny && mouseHit.Object == target && MathF.Abs(mouseHit.Distance - 8.9f) < 0.05f,
          "a raycast from the mouse finds the object under it (the ray starts on the camera's near plane)");
    Input.MouseProvider = () => default;

    // Shadow settings on the sun
    var sunScene = new Scene();
    var sunObject = new SceneObject { Name = "Sun" };
    sunObject.Components.Add(new DirectionalLight { Shadows = false, ShadowStrength = 0.6f, ShadowDistance = 90f });
    sunScene.Add(sunObject);
    var sunBack = SceneSerializer.FromJson(SceneSerializer.ToJson(sunScene)).Find(sunObject.Id)!.Components.OfType<DirectionalLight>().Single();
    Check(!sunBack.Shadows && MathF.Abs(sunBack.ShadowStrength - 0.6f) < 1e-6f && sunBack.ShadowDistance == 90f, "the sun's shadow settings survive saving");
    var defaultSun = new DirectionalLight();
    Check(defaultSun.Shadows && defaultSun.ShadowStrength == 1f, "the sun casts shadows by default");
}

// ---- Scene settings ----------------------------------------------------------------------------------------
{
    var settingsScene = new Scene { Name = "Settings" };
    var box = new SceneObject { Name = "Box" };
    box.Components.Add(new BoxCollider());
    settingsScene.Add(box);
    Check(settingsScene.Settings.ShadowResolution == 2048 && settingsScene.Settings.Shadows, "a new scene has shadows on, with a 2048 shadow map");
    settingsScene.Settings.Shadows = false;
    settingsScene.Settings.ShadowResolution = 4000;
    Check(settingsScene.Settings.ShadowResolution == 4096, "a shadow resolution is rounded to one of the choices");
    settingsScene.Physics.Gravity = new Vector3(0f, -3f, 1f);
    var settingsBack = SceneSerializer.FromJson(SceneSerializer.ToJson(settingsScene));
    Check(settingsBack.Settings.ShadowResolution == 4096 && !settingsBack.Settings.Shadows && settingsBack.Physics.Gravity == new Vector3(0f, -3f, 1f), "scene settings and gravity survive saving");

    var oldScene = SceneSerializer.FromJson("""{ "formatVersion": 1, "name": "Old", "objects": [] }""");
    Check(oldScene.Settings.ShadowResolution == 2048 && oldScene.Settings.Shadows && oldScene.Physics.Gravity == new Vector3(0f, -9.81f, 0f), "a scene saved before settings existed gets the defaults");

    var settingsPath = Path.Combine(dir, "Settings.empirescene");
    SceneSerializer.Save(settingsScene, settingsPath);
    var (readSettings, readGravity) = SceneSerializer.ReadSettings(settingsPath);
    Check(readSettings.ShadowResolution == 4096 && !readSettings.Shadows && readGravity == new Vector3(0f, -3f, 1f), "a scene file's settings can be read without loading it");
    readSettings.ShadowResolution = 512;
    readSettings.Shadows = true;
    SceneSerializer.WriteSettings(settingsPath, readSettings, new Vector3(0f, -20f, 0f));
    var rewritten = SceneSerializer.Load(settingsPath);
    Check(rewritten.Settings.ShadowResolution == 512 && rewritten.Settings.Shadows && rewritten.Physics.Gravity == new Vector3(0f, -20f, 0f) &&
          rewritten.All.Single().Name == "Box" && rewritten.All.Single().Components.OfType<BoxCollider>().Any(),
          "writing a scene file's settings keeps its objects");
}

// ---- Components written in scripts, packages, and the Water package ----------------------------------------
{
    string engine = typeof(Behaviour).Assembly.Location;
    var componentScripts = Path.Combine(dir, "component-scripts");
    if (Directory.Exists(componentScripts)) Directory.Delete(componentScripts, true);
    Directory.CreateDirectory(componentScripts);
    File.WriteAllText(Path.Combine(componentScripts, "Bobber.cs"), """
        public class Bobber : Component
        {
            public float Height = 1f;
            public Vector3 TintColor = new(1f, 0f, 0f);
            public int Starts;
            public override void Start() { Starts++; }
            public override void Update() => Object.Position += new Vector3(0f, Height, 0f);
        }
        public class Reader : Behaviour
        {
            public float Seen;
            public override void Update() { if (GetComponent<Bobber>() is { } b) Seen = b.Height; }
        }
        """);
    string componentDll = Path.Combine(dir, "component-scripts-" + Guid.NewGuid().ToString("N") + ".dll");
    Check(Engine.Scripting.Build.ScriptBuilder.Compile(componentScripts, componentDll,
              Engine.Scripting.Build.ScriptBuilder.DefaultReferences(engine), out string componentErrors),
          "a script with a class that extends Component compiles" + (componentErrors.Length > 0 ? ": " + componentErrors : ""));
    var componentAssembly = Assembly.LoadFrom(componentDll);

    var bobScene = new Scene();
    var bob = new SceneObject { Name = "Bob" };
    bob.Components.Add(new ScriptComponent { ScriptName = "Bobber", Fields = { ["Height"] = "2", ["TintColor"] = "[0, 1, 0.5]" } });
    bob.Components.Add(new ScriptComponent { ScriptName = "Reader" });
    bobScene.Add(bob);
    string loadErrors = ScriptRuntime.Load(bobScene, new[] { componentAssembly });
    var bobber = bob.Components.FirstOrDefault(c => c.GetType().Name == "Bobber");
    Check(loadErrors.Length == 0 && bobber is not null && bob.Components.IndexOf(bobber) == 1,
          "a component from a script is put on the object, after its entry" + (loadErrors.Length > 0 ? ": " + loadErrors : ""));
    Check(bobber is not null && (float)bobber.GetType().GetField("Height")!.GetValue(bobber)! == 2f &&
          (Vector3)bobber.GetType().GetField("TintColor")!.GetValue(bobber)! == new Vector3(0f, 1f, 0.5f),
          "Inspector values (numbers and Vector3s) go into the component");
    SceneUpdate.Update(bobScene, 1f / 60f);
    SceneUpdate.Update(bobScene, 1f / 60f);
    Check(MathF.Abs(bob.Position.Y - 4f) < 1e-4f && (int)bobber!.GetType().GetField("Starts")!.GetValue(bobber)! == 1,
          "the component's Start runs once and Update every frame");
    var reader = bob.Components.OfType<ScriptComponent>().Single(c => c.ScriptName == "Reader").Instance!;
    Check((float)reader.GetType().GetField("Seen")!.GetValue(reader)! == 2f, "a script finds the component with GetComponent<T>()");
    var bobJson = SceneSerializer.ToJson(bobScene);
    var bobBack = SceneSerializer.FromJson(bobJson).All.Single();
    Check(bobBack.Components.Count == 2 && bobBack.Components.OfType<ScriptComponent>().Count() == 2, "a running component is saved once, as its entry");
    var bobSaved = SceneSerializer.FromJson(SceneSerializer.ToJson(bobScene, runtimeState: true)).All.Single()
        .Components.OfType<ScriptComponent>().Single(c => c.ScriptName == "Bobber");
    Check(bobSaved.Fields["Height"] == "2", "a save game keeps the component's current values");
    var describedFields = ScriptFields.Describe(componentAssembly.GetType("Bobber")!);
    Check(describedFields.Any(f => f.Name == "TintColor" && f.Kind == ScriptFieldKind.Vector3 && f.DefaultJson == "[1,0,0]"),
          "the Inspector can describe a component's Vector3 field");

    // Packages: turned off at first, built into the project when turned on, removed when turned off.
    string repo = AppContext.BaseDirectory;
    while (!Directory.Exists(Path.Combine(repo, "Packages", "Water")) && Path.GetDirectoryName(repo) is { } up && up != repo) repo = up;
    string packagesRoot = Path.Combine(repo, "Packages");
    var project = Path.Combine(dir, "package-project");
    if (Directory.Exists(project)) Directory.Delete(project, true);
    Directory.CreateDirectory(project);
    var available = Engine.Scripting.Build.Packages.Available(packagesRoot);
    Check(available.Any(p => p.Id == "Water" && p.Description.Length > 0), "the Water package is found with its description");
    Check(Engine.Scripting.Build.Packages.Enabled(project).Count == 0, "a project starts with every package turned off");
    Engine.Scripting.Build.Packages.SetEnabled(project, "Water", true);
    var (packageDlls, packageErrors) = Engine.Scripting.Build.Packages.Build(project, engine, packagesRoot);
    string waterDll = Engine.Scripting.Build.Packages.DllPath(project, "Water");
    Check(packageErrors.Length == 0 && packageDlls.SequenceEqual(new[] { waterDll }) && File.Exists(waterDll),
          "turning a package on builds its DLL into the project" + (packageErrors.Length > 0 ? ": " + packageErrors : ""));
    var builtAt = File.GetLastWriteTimeUtc(waterDll);
    Engine.Scripting.Build.Packages.Build(project, engine, packagesRoot);
    Check(File.GetLastWriteTimeUtc(waterDll) == builtAt, "an up-to-date package is not compiled again");

    // The Water component, on a plane and on something that isn't one.
    var waterAssembly = Assembly.Load(File.ReadAllBytes(waterDll));
    MeshData Quad(float height) => new("Quad", [-1, 0, -1, 1, 0, -1, -1, height, 1, 1, height, 1], new float[12], new float[8], [0, 2, 1, 1, 2, 3],
        new MaterialData([1, 1, 1, 1], 0.5f, 0f, 1.5f, 1f, [1, 1, 1], [0, 0, 0]));
    var models = new Dictionary<string, GlbModel>
    {
        ["plane"] = new() { Name = "plane", Meshes = [Quad(0f)], BoundsMin = new(-1, 0, -1), BoundsMax = new(1, 0, 1) },
        ["ramp"] = new() { Name = "ramp", Meshes = [Quad(2f)], BoundsMin = new(-1, 0, -1), BoundsMax = new(1, 2, 1) },
    };
    var previousLoader = Engine.Core.Physics.PhysicsWorld.ModelLoader;
    Engine.Core.Physics.PhysicsWorld.ModelLoader = path => models[path];
    var waterScene = new Scene();
    var sea = new SceneObject { Name = "Sea", Scale = new(10, 1, 10) };
    sea.Components.Add(new MeshRenderer { Model = "plane" });
    sea.Components.Add(new ScriptComponent { ScriptName = "Water", Fields = { ["Resolution"] = "\"Grid512\"" } });
    var notSea = new SceneObject { Name = "Ramp" };
    notSea.Components.Add(new MeshRenderer { Model = "ramp" });
    notSea.Components.Add(new ScriptComponent { ScriptName = "Water" });
    waterScene.Add(sea);
    waterScene.Add(notSea);
    string waterErrors = ScriptRuntime.Load(waterScene, new[] { waterAssembly });
    SceneUpdate.Update(waterScene, 0.1f);
    var seaRenderer = sea.Components.OfType<MeshRenderer>().Single();
    float[] SeaVertices() => seaRenderer.Pose!.Vertices![0]!;
    float[] Part(float[] vertices, int start, int width) =>
        Enumerable.Range(0, vertices.Length / 12).SelectMany(v => Enumerable.Range(start, width).Select(k => vertices[v * 12 + k])).ToArray();
    Check(waterErrors.Length == 0 && seaRenderer.Mesh is { } seaMesh && seaMesh.Meshes[0].Positions.Length == 512 * 512 * 3 &&
          seaRenderer.Pose is not null && SeaVertices().Length == 512 * 512 * 12,
          "Water turns a plane into a finer grid drawn in its place");
    Check(seaRenderer.Mesh!.Meshes[0].Material.AlphaBlend && MathF.Abs(seaRenderer.Mesh.Meshes[0].Material.BaseColor[3] - 0.85f) < 1e-5f,
          "Water is see-through by default");
    float[] before = Part(SeaVertices(), 0, 3);
    SceneUpdate.Update(waterScene, 0.5f);
    float[] after = Part(SeaVertices(), 0, 3);
    Check(before.Zip(after).Any(p => MathF.Abs(p.First - p.Second) > 1e-3f) && after.Where((_, i) => i % 3 == 1).Any(y => MathF.Abs(y) > 0.01f),
          "the water's surface moves with waves over time");
    var resolutionField = ScriptFields.Describe(waterAssembly.GetType("Water")!).Single(f => f.Name == "Resolution");
    Check(resolutionField.Kind == ScriptFieldKind.Enum && resolutionField.Options.Count == 7 && resolutionField.OptionLabels[0] == "256 x 256" &&
          resolutionField.OptionLabels[^1].StartsWith("16384 x 16384") && resolutionField.DefaultJson == "\"Grid256\"",
          "Water's resolution is a dropdown from 256 to 16384, doubling each step, starting at 256");
    var qualityField = ScriptFields.Describe(waterAssembly.GetType("Water")!).Single(f => f.Name == "Quality");
    Check(qualityField.Kind == ScriptFieldKind.Enum && qualityField.DefaultJson == "\"High\"" && qualityField.OptionLabels.Contains("Ultra (32 waves)"),
          "Water's simulation quality is a dropdown, High (16 waves) by default");
    Check(seaRenderer.Mesh!.Meshes[0].Material.NormalImage is { Width: 256 } && Part(SeaVertices(), 6, 2).Any(u => u != 0f),
          "Water has a ripple texture with coordinates that follow the surface");
    var seaColors = Part(SeaVertices(), 8, 4);
    Check(seaColors.Where((_, i) => i % 4 == 1).Max() - seaColors.Where((_, i) => i % 4 == 1).Min() > 0.05f, "the water's color varies from troughs to crests and foam");
    // Foam: some points are close to the foam color (0.88 red), but only a small share of the surface.
    int foamy = 0, points = seaColors.Length / 4;
    for (int i = 0; i < points; i++) if (seaColors[i * 4] > 0.5f) foamy++;
    Check(foamy > points / 400 && foamy < points / 4, $"foam covers some of the water, not none and not most of it ({foamy * 100.0 / points:F1}%)");
    Check(seaColors.Length == 512 * 512 * 4 && Part(SeaVertices(), 4, 1).All(y => y > 0f),
          "the water has colors and upward-facing normals");
    Check(notSea.Components.OfType<MeshRenderer>().Single().Mesh is null, "Water leaves a shape that isn't a plane alone");
    Engine.Core.Physics.PhysicsWorld.ModelLoader = previousLoader;

    Engine.Scripting.Build.Packages.SetEnabled(project, "Water", false);
    Check(!File.Exists(waterDll) && Engine.Scripting.Build.Packages.Enabled(project).Count == 0, "turning a package off removes its DLL");
}

// ---- Finding objects by name, and object fields --------------------------------------------------------------
{
    var findScene = new Scene();
    var player = new SceneObject { Name = "Player" };
    var enemy = new SceneObject { Name = "Enemy" };
    var enemy2 = new SceneObject { Name = "Enemy" };
    var holder = new SceneObject { Name = "Holder" };
    findScene.Add(player);
    findScene.Add(enemy);
    findScene.Add(enemy2, player);
    findScene.Add(holder);
    // Top to bottom as the Hierarchy shows them: Player's child comes before the Enemy below Player.
    Check(findScene.FindByName("Enemy") == enemy2 && findScene.FindAllByName("Enemy").SequenceEqual(new[] { enemy2, enemy }) &&
          findScene.FindByName("enemy") is null, "objects are found by their exact name, top to bottom as the Hierarchy lists them");

    var refScripts = Path.Combine(dir, "ref-scripts");
    if (Directory.Exists(refScripts)) Directory.Delete(refScripts, true);
    Directory.CreateDirectory(refScripts);
    File.WriteAllText(Path.Combine(refScripts, "Follower.cs"), """
        public class Follower : Behaviour
        {
            public SceneObject Target;
            public SceneObject Found;
            public SceneObject Missing;
            public override void Start()
            {
                Found = Scene.FindObjectWithName("Enemy");
                Missing = Scene.FindObjectWithName("Nobody");
            }
            public override void Update() { if (Target != null) Object.Position = Target.Position; }
        }
        """);
    string refDll = Path.Combine(dir, "ref-" + Guid.NewGuid().ToString("N") + ".dll");
    Check(Engine.Scripting.Build.ScriptBuilder.Compile(refScripts, refDll,
              Engine.Scripting.Build.ScriptBuilder.DefaultReferences(typeof(Behaviour).Assembly.Location), out string refErrors),
          "a script with a SceneObject field and Scene.FindObjectWithName compiles" + (refErrors.Length > 0 ? ": " + refErrors : ""));
    var refAssembly = Assembly.LoadFrom(refDll);
    var describedRef = ScriptFields.Describe(refAssembly.GetType("Follower")!);
    Check(describedRef.Any(f => f.Name == "Target" && f.Kind == ScriptFieldKind.Object && f.DefaultJson == "null"),
          "a SceneObject field shows in the Inspector, empty by default");

    player.Position = new Vector3(3, 4, 5);
    var follower = new ScriptComponent { ScriptName = "Follower", Fields = { ["Target"] = ScriptFields.ObjectJson(player) } };
    holder.Components.Add(follower);
    var savedScene = SceneSerializer.FromJson(SceneSerializer.ToJson(findScene));   // the field survives saving as the id
    var savedHolder = savedScene.Find(holder.Id)!;
    string loadErrors = ScriptRuntime.Load(savedScene, new[] { refAssembly });
    SceneUpdate.Update(savedScene, 1f / 60f);
    var live = savedHolder.Components.OfType<ScriptComponent>().Single().Instance!;
    var type = live.GetType();
    Check(loadErrors.Length == 0 && type.GetField("Target")!.GetValue(live) == savedScene.Find(player.Id),
          "the dragged-in object becomes the object itself when play starts");
    Check(savedHolder.Position == new Vector3(3, 4, 5), "the script can use the object it was given");
    Check(type.GetField("Found")!.GetValue(live) == savedScene.Find(enemy2.Id) && type.GetField("Missing")!.GetValue(live) is null,
          "Scene.FindObjectWithName finds an object while playing, or gives null");
    var captured = ScriptFields.Capture(live);
    Check(captured["Target"] == ScriptFields.ObjectJson(player) && captured["Missing"] == "null", "a save game keeps object fields as ids");

    // An object field pointing at something deleted is null, with a note.
    var gone = new Scene();
    var lonely = new SceneObject { Name = "Lonely" };
    lonely.Components.Add(new ScriptComponent { ScriptName = "Follower", Fields = { ["Target"] = ScriptFields.ObjectJson(new SceneObject()) } });
    gone.Add(lonely);
    ScriptRuntime.Load(gone, new[] { refAssembly });
    var lonelyScript = lonely.Components.OfType<ScriptComponent>().Single().Instance!;
    Check(lonelyScript.GetType().GetField("Target")!.GetValue(lonelyScript) is null, "a field pointing at a deleted object is null");
}

// ---- Lists and arrays in the Inspector -------------------------------------------------------------------------
{
    var listScripts = Path.Combine(dir, "list-scripts");
    if (Directory.Exists(listScripts)) Directory.Delete(listScripts, true);
    Directory.CreateDirectory(listScripts);
    File.WriteAllText(Path.Combine(listScripts, "Lists.cs"), """
        public class Lists : Behaviour
        {
            public float[] Speeds = { 1f, 2f };
            public List<int> Counts = new();
            public string[] Names = { "a" };
            public List<Vector3> Points = new() { new Vector3(1, 2, 3) };
            public SceneObject[] Targets;
            public List<SceneObject> Others = new();
        }
        """);
    string listDll = Path.Combine(dir, "lists-" + Guid.NewGuid().ToString("N") + ".dll");
    Check(Engine.Scripting.Build.ScriptBuilder.Compile(listScripts, listDll,
              Engine.Scripting.Build.ScriptBuilder.DefaultReferences(typeof(Behaviour).Assembly.Location), out string listErrors),
          "a script with lists and arrays compiles" + (listErrors.Length > 0 ? ": " + listErrors : ""));
    var listAssembly = Assembly.LoadFrom(listDll);
    var listFields = ScriptFields.Describe(listAssembly.GetType("Lists")!).ToDictionary(f => f.Name);
    Check(listFields["Speeds"] is { Kind: ScriptFieldKind.Float, IsArray: true, DefaultJson: "[1,2]" } &&
          listFields["Counts"] is { Kind: ScriptFieldKind.Int, IsList: true, DefaultJson: "[]" } &&
          listFields["Names"] is { Kind: ScriptFieldKind.String, IsArray: true } &&
          listFields["Points"] is { Kind: ScriptFieldKind.Vector3, IsList: true, DefaultJson: "[[1,2,3]]" } &&
          listFields["Targets"] is { Kind: ScriptFieldKind.Object, IsArray: true, DefaultJson: "[]" } &&
          listFields["Others"] is { Kind: ScriptFieldKind.Object, IsList: true, DefaultJson: "[]" },
          "lists and arrays of numbers, strings, Vector3s and objects show in the Inspector");

    var listScene = new Scene();
    var a = new SceneObject { Name = "A" };
    var b = new SceneObject { Name = "B" };
    var owner = new SceneObject { Name = "Owner" };
    listScene.Add(a); listScene.Add(b); listScene.Add(owner);
    owner.Components.Add(new ScriptComponent
    {
        ScriptName = "Lists",
        Fields =
        {
            ["Speeds"] = "[3,4,5]",
            ["Counts"] = "[7]",
            ["Points"] = "[[0,1,0],[2,2,2]]",
            ["Targets"] = $"[{ScriptFields.ObjectJson(b)},null,{ScriptFields.ObjectJson(a)}]",
            ["Others"] = $"[{ScriptFields.ObjectJson(a)}]",
        },
    });
    var listBack = SceneSerializer.FromJson(SceneSerializer.ToJson(listScene));
    ScriptRuntime.Load(listBack, new[] { listAssembly });
    var lists = listBack.Find(owner.Id)!.Components.OfType<ScriptComponent>().Single().Instance!;
    var t = lists.GetType();
    var targets = (SceneObject?[])t.GetField("Targets")!.GetValue(lists)!;
    Check(((float[])t.GetField("Speeds")!.GetValue(lists)!).SequenceEqual(new[] { 3f, 4f, 5f }) &&
          ((List<int>)t.GetField("Counts")!.GetValue(lists)!).SequenceEqual(new[] { 7 }) &&
          ((List<Vector3>)t.GetField("Points")!.GetValue(lists)!)[1] == new Vector3(2, 2, 2),
          "list values set in the Inspector reach the script");
    Check(targets.Length == 3 && targets[0] == listBack.Find(b.Id) && targets[1] is null && targets[2] == listBack.Find(a.Id) &&
          ((List<SceneObject>)t.GetField("Others")!.GetValue(lists)!).Single() == listBack.Find(a.Id),
          "object lists become the objects themselves, empty entries included");
    var listCaptured = ScriptFields.Capture(lists);
    Check(ScriptFields.ObjectIds(listCaptured["Targets"]).SequenceEqual(new Guid?[] { b.Id, null, a.Id }), "a save game keeps object lists as ids");
}

// ---- Game UI: clicks, typing, dropdowns, and control fields in scripts ---------------------------------------
{
    var uiScripts = Path.Combine(dir, "ui-scripts");
    if (Directory.Exists(uiScripts)) Directory.Delete(uiScripts, true);
    Directory.CreateDirectory(uiScripts);
    File.WriteAllText(Path.Combine(uiScripts, "Menu.cs"), """
        public class Menu : Behaviour
        {
            public Button play;
            public TextField playerName;
            public Checkbox sound;
            public Dropdown mode;
            public Bar health;
            public List<Button> extras = new();
            public Button wrongKind;

            public int clicks;
            public string typed = "";
            public string submitted = "";
            public bool soundOn;
            public int picked = -1;

            public override void Start()
            {
                play.OnClick += () => throw new System.Exception("boom");   // a failing handler must not stop the others
                play.OnClick += () => clicks++;
                playerName.OnChanged += text => typed = text;
                playerName.OnSubmit += text => submitted = text;
                sound.OnChanged += on => soundOn = on;
                mode.OnChanged += index => picked = index;
                health.Value = 0.25f;
            }
        }
        """);
    string uiDll = Path.Combine(dir, "ui-" + Guid.NewGuid().ToString("N") + ".dll");
    Check(Engine.Scripting.Build.ScriptBuilder.Compile(uiScripts, uiDll,
              Engine.Scripting.Build.ScriptBuilder.DefaultReferences(typeof(Behaviour).Assembly.Location), out string uiErrors),
          "a script with Button, TextField, Checkbox, Dropdown and Bar fields compiles" + (uiErrors.Length > 0 ? ": " + uiErrors : ""));
    var uiAssembly = Assembly.LoadFrom(uiDll);
    var uiFields = ScriptFields.Describe(uiAssembly.GetType("Menu")!);
    Check(uiFields.Any(f => f.Name == "play" && f.Kind == ScriptFieldKind.Object && f.RequiredUi == UiKind.Button && f.DefaultJson == "null") &&
          uiFields.Any(f => f.Name == "mode" && f.RequiredUi == UiKind.Dropdown) &&
          uiFields.Any(f => f.Name == "extras" && f.IsList && f.RequiredUi == UiKind.Button && f.DefaultJson == "[]"),
          "control fields show in the Inspector as object slots that need a UI element of their kind");

    var uiScene = new Scene();
    SceneObject AddUiObject(string name, UiKind kind, float x, float y)
    {
        var obj = new SceneObject { Name = name };
        obj.Components.Add(new UiElement { Kind = kind, Label = name, X = x, Y = y, W = 0.2f, H = 0.1f, Options = new() { "Easy", "Normal", "Hard" } });
        uiScene.Add(obj);
        return obj;
    }
    var playObj = AddUiObject("Play", UiKind.Button, 0.1f, 0.1f);       // 800 x 450 view: (80, 45) to (240, 90)
    var nameObj = AddUiObject("Name", UiKind.TextField, 0.1f, 0.3f);
    var soundObj = AddUiObject("Sound", UiKind.Checkbox, 0.1f, 0.5f);
    var modeObj = AddUiObject("Mode", UiKind.Dropdown, 0.5f, 0.1f);     // options open below, 45 points each
    var healthObj = AddUiObject("Health", UiKind.Bar, 0.5f, 0.5f);
    var quitObj = AddUiObject("Quit", UiKind.Button, 0.5f, 0.7f);
    quitObj.Components.OfType<UiElement>().Single().Interactable = false;
    var menuObj = new SceneObject { Name = "Menu" };
    menuObj.Components.Add(new ScriptComponent
    {
        ScriptName = "Menu",
        Fields =
        {
            ["play"] = ScriptFields.ObjectJson(playObj),
            ["playerName"] = ScriptFields.ObjectJson(nameObj),
            ["sound"] = ScriptFields.ObjectJson(soundObj),
            ["mode"] = ScriptFields.ObjectJson(modeObj),
            ["health"] = ScriptFields.ObjectJson(healthObj),
            ["extras"] = $"[{ScriptFields.ObjectJson(quitObj)},{ScriptFields.ObjectJson(playObj)}]",
            ["wrongKind"] = ScriptFields.ObjectJson(nameObj),   // a text field dropped on a Button slot
        },
    });
    uiScene.Add(menuObj);

    string uiJson = SceneSerializer.ToJson(uiScene);
    Check(!uiJson.Contains("\"visible\"") && uiJson.Contains("\"interactable\": false"),
          "UI Visible and Interactable are saved only when they are off");
    var uiPlaying = SceneSerializer.FromJson(uiJson);
    Check(!uiPlaying.Find(quitObj.Id)!.Components.OfType<UiElement>().Single().Interactable, "Interactable survives saving");

    string uiLoad = ScriptRuntime.Load(uiPlaying, new[] { uiAssembly });
    Check(uiLoad.Length == 0, "a menu script with control fields loads" + (uiLoad.Length > 0 ? ": " + uiLoad : ""));
    string wrongNote = ScriptFields.Apply(Activator.CreateInstance(uiAssembly.GetType("Menu")!)!,
        new Dictionary<string, string> { ["wrongKind"] = ScriptFields.ObjectJson(nameObj) }, uiPlaying.Find);
    Check(wrongNote.Contains("'Name' has no UI Button"), "a control field given the wrong kind of object gets a note (and stays null)");
    var menu = uiPlaying.Find(menuObj.Id)!.Components.OfType<ScriptComponent>().Single().Instance!;
    T Get<T>(string field) => (T)menu.GetType().GetField(field)!.GetValue(menu)!;
    UiElement El(SceneObject obj) => uiPlaying.Find(obj.Id)!.Components.OfType<UiElement>().Single();

    var mouse = new MouseState(Vector2.Zero, false, false, false, 0f);
    var keysDown = new HashSet<Keys>();
    string typing = "";
    Input.MouseProvider = () => mouse;
    Input.Provider = key => keysDown.Contains(key);
    Input.TextProvider = () => { var t = typing; typing = ""; return t; };
    Input.SetView(new Vector2(800, 450), Matrix4x4.Identity);
    void Frame() => SceneUpdate.Update(uiPlaying, 1f / 60f);
    void ClickAt(float x, float y)
    {
        mouse = mouse with { Position = new Vector2(x, y), Left = true };
        Frame();
        mouse = mouse with { Left = false };
        Frame();
    }

    Frame();   // Start runs and adds the handlers
    var play = Get<Engine.Core.Scene.Button>("play");
    Check(play.Object == uiPlaying.Find(playObj.Id) && Get<object?>("wrongKind") is null &&
          Get<List<Engine.Core.Scene.Button>>("extras").Count == 2 && Get<List<Engine.Core.Scene.Button>>("extras")[1].Object == play.Object,
          "control fields become the controls of the dragged-in objects, lists included");
    Check(El(healthObj).Value == 0.25f, "a script sets a bar's value through its Bar field");

    mouse = mouse with { Position = new Vector2(160, 67), Left = true };
    Frame();
    Check(GameUi.Pressed == El(playObj) && GameUi.PointerOverUi && Get<int>("clicks") == 0, "pressing a button holds it without clicking yet");
    mouse = mouse with { Left = false };
    Frame();
    Check(Get<int>("clicks") == 1 && El(playObj).Clicked && play.Clicked, "letting go over the button runs OnClick, even after another handler threw");
    Frame();
    Check(!El(playObj).Clicked && Get<int>("clicks") == 1, "Clicked lasts one frame and the click is not repeated");

    mouse = mouse with { Position = new Vector2(160, 67), Left = true };
    Frame();
    mouse = mouse with { Position = new Vector2(600, 400), Left = false };
    Frame();
    Check(Get<int>("clicks") == 1, "pressing a button and letting go elsewhere does not click it");

    ClickAt(160, 157);
    Check(GameUi.Focused == El(nameObj) && GameUi.IsTyping && Get<TextField>("playerName").IsFocused, "clicking a text field gives it the keyboard");
    typing = "Bobby";
    Frame();
    Check(Get<string>("typed") == "Bobby" && El(nameObj).Text == "Bobby", "typed characters go into the field and run OnChanged");
    keysDown.Add(Keys.Backspace);
    Frame();
    keysDown.Clear();
    Frame();
    Check(Get<string>("typed") == "Bobb", "Backspace deletes a character");
    keysDown.Add(Keys.Enter);
    Frame();
    keysDown.Clear();
    Check(Get<string>("submitted") == "Bobb" && !GameUi.IsTyping, "Enter runs OnSubmit and lets go of the keyboard");
    typing = "zzz";
    Frame();
    Check(El(nameObj).Text == "Bobb", "typing after the field let go changes nothing");

    ClickAt(160, 247);
    Check(Get<bool>("soundOn") && El(soundObj).Checked && Get<Checkbox>("sound").IsChecked, "clicking a checkbox ticks it and runs OnChanged");

    ClickAt(480, 67);
    Check(GameUi.OpenDropdown == El(modeObj) && Get<Dropdown>("mode").IsOpen, "clicking a dropdown opens its list");
    var hardRow = GameUi.OptionRect(El(modeObj), 2, new Vector2(800, 450));
    ClickAt(hardRow.X + 10, hardRow.Y + hardRow.W / 2);
    Check(Get<int>("picked") == 2 && Get<Dropdown>("mode").SelectedText == "Hard" && GameUi.OpenDropdown is null,
          "picking an option sets Selected, runs OnChanged and closes the list");
    ClickAt(480, 67);
    ClickAt(700, 420);
    Check(GameUi.OpenDropdown is null && Get<int>("picked") == 2, "clicking away closes the list without changing the choice");

    ClickAt(480, 337);   // the Quit button, which is not interactable
    Check(!El(quitObj).Clicked && GameUi.Pressed is null, "a button that is not interactable takes no clicks");
    play.Visible = false;
    ClickAt(160, 67);
    Check(Get<int>("clicks") == 1 && !GameUi.PointerOverUi, "a hidden button takes no clicks and the mouse is not over the UI");
    play.Visible = true;
    play.Click();
    Check(Get<int>("clicks") == 2, "Button.Click runs the handlers from code");

    Input.cursorLocked = true;
    ClickAt(160, 67);
    Check(Get<int>("clicks") == 2 && !GameUi.PointerOverUi, "the UI ignores the mouse while the cursor is locked for mouse-look");
    Input.cursorLocked = false;

    var menuSaved = ScriptFields.Capture(menu);
    Check(menuSaved["play"] == ScriptFields.ObjectJson(playObj) &&
          ScriptFields.ObjectIds(menuSaved["extras"]).SequenceEqual(new Guid?[] { quitObj.Id, playObj.Id }) && menuSaved["wrongKind"] == "null",
          "a save game keeps control fields as the objects' ids");
    Check(UiControl.From<Dropdown>(uiPlaying.Find(modeObj.Id)) is { Selected: 2 } && UiControl.From<Engine.Core.Scene.Button>(uiPlaying.Find(modeObj.Id)) is null,
          "UiControl.From finds a control on an object, or gives null for the wrong kind");

    Input.MouseProvider = () => default;
    Input.Provider = _ => false;
    Input.TextProvider = () => "";
}

// ---- Installing Empire versions from GitHub releases (the hub's Installs page) -----------------------------------
{
    byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);
    byte[] MakeZip(params (string Name, byte[] Data, int Mode)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data, mode) in files)
            {
                var e = z.CreateEntry(name);
                if (mode != 0) e.ExternalAttributes = mode << 16;   // a zip made on macOS or Linux keeps permissions up here
                using var s = e.Open();
                s.Write(data);
            }
        return ms.ToArray();
    }
    string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    const int Exec = 0x81ED, Plain = 0x81A4;   // regular file, rwxr-xr-x and rw-r--r--
    string[] Leftovers(string root) => Directory.Exists(root)
        ? Directory.GetFileSystemEntries(root).Where(p => Path.GetFileName(p).StartsWith(".download-") || Path.GetFileName(p).StartsWith(".installing-")).ToArray()
        : [];
    string NewRoot() => Path.Combine(dir, "installs-" + Guid.NewGuid().ToString("N"));

    // --- reading GitHub's list of releases ---
    string releaseJson = """
        [
          { "tag_name": "v0.1.1", "name": "Empire 0.1.1", "draft": false, "prerelease": false, "published_at": "2026-10-20T10:00:00Z",
            "html_url": "https://github.com/ardenarrison/EmpireEngine/releases/tag/v0.1.1", "body": "Fixes.",
            "assets": [
              { "name": "Empire-0.1.1-osx-arm64.zip", "size": 1234, "state": "uploaded", "browser_download_url": "https://github.com/x/releases/download/v0.1.1/Empire-0.1.1-osx-arm64.zip", "digest": "sha256:ABCDEF0123" },
              { "name": "Empire-0.1.1-win-x64.zip", "size": 1300, "state": "uploaded", "browser_download_url": "https://github.com/x/releases/download/v0.1.1/Empire-0.1.1-win-x64.zip" },
              { "name": "Source code (zip)", "size": 9, "state": "uploaded", "browser_download_url": "https://github.com/x/zipball/v0.1.1" } ] },
          { "tag_name": "v0.2.0", "name": "Empire 0.2.0", "draft": false, "prerelease": false, "published_at": "2026-11-01T10:00:00Z",
            "assets": [ { "name": "Empire-0.2.0-win-x64.zip", "size": 5, "state": "uploaded", "browser_download_url": "https://github.com/x/Empire-0.2.0-win-x64.zip" } ] },
          { "tag_name": "v0.2.0-beta", "name": "", "draft": false, "prerelease": true, "published_at": "2026-10-28T10:00:00Z", "assets": [] },
          { "tag_name": "0.0.9", "draft": false, "prerelease": false, "assets": [] },
          { "tag_name": "nightly", "draft": false, "prerelease": false, "assets": [] },
          { "tag_name": "v9.9.9", "draft": true, "prerelease": false, "assets": [] }
        ]
        """;
    var parsed = ReleaseCatalog.Parse(releaseJson, "osx-arm64");
    Check(parsed.Select(r => r.Version).SequenceEqual(new[] { "0.2.0", "0.2.0-beta", "0.1.1", "0.0.9" }),
          "releases are listed newest first, a final release before its beta, and drafts and non-version tags are left out");
    Check(parsed.Where(r => r.IsLatest).Select(r => r.Version).SequenceEqual(new[] { "0.2.0" }) && parsed.Single(r => r.Version == "0.2.0-beta").Prerelease,
          "the newest final release is marked latest, and a beta is marked pre-release");
    Check(parsed.Single(r => r.Version == "0.1.1").Asset is { Name: "Empire-0.1.1-osx-arm64.zip", Size: 1234, Sha256: "abcdef0123" },
          "a release's download for this platform is found, with its checksum in lower case");
    Check(parsed.Single(r => r.Version == "0.2.0").Asset is null && ReleaseCatalog.Parse(releaseJson, "win-x64").Single(r => r.Version == "0.2.0").Asset is not null,
          "a release without a zip for this platform has no download, while another platform finds it");
    Check(ReleaseCatalog.Parse(releaseJson, "osx-arm64").Single(r => r.Version == "0.1.1").Asset!.Url.Contains("osx-arm64") &&
          ReleaseCatalog.Parse(releaseJson, "linux-x64").All(r => r.Asset is null), "the 'Source code (zip)' GitHub adds is never offered as an install");
    // The current zip name (EmpireEngine-...) is preferred, the 0.1.0 name (Empire-...) still installs, and the Hub's own zip never counts.
    string AssetJson(params string[] names) => "[{ \"tag_name\": \"v0.3.0\", \"draft\": false, \"prerelease\": false, \"assets\": [" +
        string.Join(",", names.Select(n => $"{{ \"name\": \"{n}\", \"size\": 1, \"state\": \"uploaded\", \"browser_download_url\": \"https://github.com/x/{n}\" }}")) + "] }]";
    string? Picked(params string[] names) => ReleaseCatalog.Parse(AssetJson(names), "win-x64").Single().Asset?.Name;
    Check(Picked("EmpireEngineHub-0.3.0-win-x64.zip", "Empire-0.3.0-win-x64.zip", "EmpireEngine-0.3.0-win-x64.zip") == "EmpireEngine-0.3.0-win-x64.zip" &&
          Picked("EmpireHub-0.1.0-win-x64.zip", "Empire-0.1.0-win-x64.zip") == "Empire-0.1.0-win-x64.zip" &&
          Picked("EmpireEngineHub-0.3.0-win-x64.zip", "EmpireHub-0.3.0-win-x64.zip") is null,
          "the editor zip is EmpireEngine-<version>-<platform>.zip, the older Empire- name still installs, and the Hub's own zip is never picked");
    Check(ReleaseCatalog.VersionOf("v1.2.3") == "1.2.3" && ReleaseCatalog.VersionOf("1.2") == "1.2" && ReleaseCatalog.VersionOf("v1.0.0-rc.1") == "1.0.0-rc.1" &&
          ReleaseCatalog.VersionOf("latest") is null && ReleaseCatalog.VersionOf("v1.x") is null && ReleaseCatalog.VersionOf("") is null,
          "tags that are versions are recognised, with or without a leading v");
    bool badJson = false;
    try { ReleaseCatalog.Parse("this is not json", "osx-arm64"); } catch (InstallException) { badJson = true; }
    Check(badJson, "an answer that isn't JSON is a readable error, not a crash");

    // --- asking GitHub (a fake one) ---
    async Task<string> FetchError(HttpStatusCode code)
    {
        using var http = new HttpClient(new FakeGitHub(_ => new HttpResponseMessage(code)));
        try { await ReleaseCatalog.FetchAsync(http, CancellationToken.None, "osx-arm64"); return ""; }
        catch (InstallException ex) { return ex.Message; }
    }
    using (var ok = new HttpClient(new FakeGitHub(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson), RequestMessage = r })))
        Check((await ReleaseCatalog.FetchAsync(ok, CancellationToken.None, "osx-arm64")).Count == 4, "the hub reads the releases GitHub answers with");
    Check((await FetchError(HttpStatusCode.NotFound)).Contains("no releases") && (await FetchError(HttpStatusCode.Forbidden)).Contains("too many requests") &&
          (await FetchError(HttpStatusCode.InternalServerError)).Contains("500"), "a missing repository, a rate limit and a server error each get their own plain message");
    using (var offline = new HttpClient(new FakeGitHub(_ => throw new HttpRequestException("no route"))))
    {
        string offlineMessage = "";
        try { await ReleaseCatalog.FetchAsync(offline, CancellationToken.None, "osx-arm64"); } catch (InstallException ex) { offlineMessage = ex.Message; }
        Check(offlineMessage.Contains("Couldn't reach GitHub"), "no internet connection is explained");
    }

    // --- installing ---
    var editorFiles = new (string, byte[], int)[]
    {
        ("Empire/0.1.1/empire.dll", Bytes("dll"), Plain), ("Empire/0.1.1/empire", Bytes("host"), Exec),
        ("Empire/0.1.1/Player/Player", Bytes("player"), Plain),     // stored without the executable permission on purpose
        ("Empire/0.1.1/Licenses/NOTICE.txt", Bytes("notice"), Plain), ("Empire/0.1.1/Fonts/", Array.Empty<byte>(), 0),
        ("EmpireHub", Bytes("the hub, which is not an editor"), Exec), ("Empire/0.0.5/empire.dll", Bytes("older"), Plain),
    };
    byte[] goodZip = MakeZip(editorFiles);
    string goodUrl = "https://github.com/ardenarrison/EmpireEngine/releases/download/v0.1.1/Empire-0.1.1-osx-arm64.zip";
    EmpireRelease Release(string version, byte[] zip, string? sha, string url = "") => new()
    {
        Tag = "v" + version, Version = version, Title = "Empire " + version,
        Asset = new ReleaseAsset($"Empire-{version}-osx-arm64.zip", url.Length > 0 ? url : goodUrl, zip.Length, sha),
    };
    HttpClient Serving(byte[] zip, out FakeGitHub fake)
    {
        fake = new FakeGitHub(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip), RequestMessage = r });
        return new HttpClient(fake);
    }
    string InstallError(Func<Task> install)
    {
        try { install().GetAwaiter().GetResult(); return ""; }
        catch (InstallException ex) { return ex.Message; }
    }

    string installsRoot = NewRoot();
    var reports = new List<InstallProgress>();
    using (var http = Serving(goodZip, out _))
    {
        string installed = await ReleaseInstaller.InstallAsync(http, Release("0.1.1", goodZip, Sha(goodZip)), installsRoot, new SyncProgress(reports.Add), CancellationToken.None);
        Check(installed == Path.Combine(installsRoot, "0.1.1") && File.Exists(Path.Combine(installed, "empire.dll")) && File.ReadAllText(Path.Combine(installed, "Licenses", "NOTICE.txt")) == "notice" &&
              Directory.Exists(Path.Combine(installed, "Fonts")), "an install lands in <installs>/<version>/ with the editor's files and folders");
        Check(!File.Exists(Path.Combine(installsRoot, "EmpireHub")) && !Directory.Exists(Path.Combine(installsRoot, "0.0.5")) && !File.Exists(Path.Combine(installed, "EmpireHub")),
              "only the Empire/<version>/ part of the zip is unpacked, not the hub or other versions");
        var marker = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(installed, ReleaseInstaller.MarkerFile)), ReleaseJson.Default.InstallMarker)!;
        Check(marker.Version == "0.1.1" && marker.Tag == "v0.1.1" && marker.Sha256 == Sha(goodZip), "an install records where it came from, so the hub can tell it from the bundled one");
        Check(Leftovers(installsRoot).Length == 0, "no download or staging files are left behind");
        Check(reports.Any(p => p.Stage == "Downloading" && p.Fraction >= 1.0) && reports.Any(p => p.Stage == "Unpacking") && reports[^1].Stage == "Done",
              "progress goes from downloading through unpacking to done");
        if (!OperatingSystem.IsWindows())
            Check(File.GetUnixFileMode(Path.Combine(installed, "empire")).HasFlag(UnixFileMode.UserExecute) &&
                  File.GetUnixFileMode(Path.Combine(installed, "Player", "Player")).HasFlag(UnixFileMode.UserExecute) &&
                  !File.GetUnixFileMode(Path.Combine(installed, "empire.dll")).HasFlag(UnixFileMode.UserExecute),
                  "the editor's launcher can be run (the zip's permissions are kept, and the launchers are made executable regardless)");
        Check(ReleaseInstaller.InstalledVersions(installsRoot).SetEquals(new[] { "0.1.1" }), "the install is found as an installed version");
        Check(InstallError(() => ReleaseInstaller.InstallAsync(http, Release("0.1.1", goodZip, Sha(goodZip)), installsRoot, null, CancellationToken.None)).Contains("already installed"),
              "installing a version that is installed already is refused");
    }

    // The zip can name a different version than the tag; what is inside is what counts.
    string rootRetag = NewRoot();
    using (var http = Serving(goodZip, out _))
        Check(Path.GetFileName(await ReleaseInstaller.InstallAsync(http, Release("0.1.2", goodZip, null), rootRetag, null, CancellationToken.None)) == "0.1.1",
              "a release whose zip holds another version number is installed under the version the zip holds");

    string rootBad = NewRoot();
    using (var http = Serving(goodZip, out _))
    {
        string wrongSum = InstallError(() => ReleaseInstaller.InstallAsync(http, Release("0.1.1", goodZip, Sha(Bytes("something else"))), rootBad, null, CancellationToken.None));
        Check(wrongSum.Contains("checksum") && !Directory.Exists(Path.Combine(rootBad, "0.1.1")) && Leftovers(rootBad).Length == 0,
              "a download that doesn't match GitHub's checksum is refused and thrown away");
    }

    byte[] Slip(string entryName) => MakeZip(("Empire/0.1.1/empire.dll", Bytes("dll"), Plain), (entryName, Bytes("pwned"), Plain));
    foreach (var evil in new[] { "Empire/0.1.1/../../evil.txt", "Empire/0.1.1/..\\..\\evil.txt", "Empire/0.1.1/sub/../../../evil.txt", "Empire/0.1.1/C:/evil.txt" })
    {
        string rootSlip = NewRoot();
        byte[] zip = Slip(evil);
        using var http = Serving(zip, out _);
        string error = InstallError(() => ReleaseInstaller.InstallAsync(http, Release("0.1.1", zip, null), rootSlip, null, CancellationToken.None));
        Check(error.Contains("unsafe path") && !File.Exists(Path.Combine(dir, "evil.txt")) && !File.Exists(Path.Combine(Path.GetDirectoryName(rootSlip)!, "evil.txt")) &&
              !Directory.Exists(Path.Combine(rootSlip, "0.1.1")) && Leftovers(rootSlip).Length == 0,
              $"a zip that tries to write outside the install folder ({evil.Replace('\\', '/')}) is refused and nothing is written");
    }

    byte[] linkZip = MakeZip(("Empire/0.1.1/empire.dll", Bytes("dll"), Plain), ("Empire/0.1.1/link", Bytes("/etc/passwd"), 0xA1FF));
    using (var http = Serving(linkZip, out _))
        Check(InstallError(() => ReleaseInstaller.InstallAsync(http, Release("0.1.1", linkZip, null), NewRoot(), null, CancellationToken.None)).Contains("symbolic link"),
              "a zip containing a symbolic link is refused");

    byte[] notEditor = MakeZip(("EmpireHub", Bytes("hub"), Exec), ("README.txt", Bytes("hi"), Plain));
    using (var http = Serving(notEditor, out _))
        Check(InstallError(() => ReleaseInstaller.InstallAsync(http, Release("0.1.1", notEditor, null), NewRoot(), null, CancellationToken.None)).Contains("doesn't contain an Empire editor"),
              "a zip with no editor in it is refused with a clear message");

    byte[] notZip = Bytes("this is not a zip file at all");
    using (var http = Serving(notZip, out _))
    {
        string rootNotZip = NewRoot();
        Check(InstallError(() => ReleaseInstaller.InstallAsync(http, Release("0.1.1", notZip, null), rootNotZip, null, CancellationToken.None)).Contains("install failed") && Leftovers(rootNotZip).Length == 0,
              "a download that isn't a zip fails cleanly and leaves nothing behind");
    }

    using (var http = Serving(goodZip, out var fake))
    {
        string rootHost = NewRoot();
        string offSite = InstallError(() => ReleaseInstaller.InstallAsync(http, Release("0.1.1", goodZip, null, "https://evil.example.com/Empire-0.1.1-osx-arm64.zip"), rootHost, null, CancellationToken.None));
        string insecure = InstallError(() => ReleaseInstaller.InstallAsync(http, Release("0.1.1", goodZip, null, "http://github.com/Empire-0.1.1-osx-arm64.zip"), rootHost, null, CancellationToken.None));
        string lookalike = InstallError(() => ReleaseInstaller.InstallAsync(http, Release("0.1.1", goodZip, null, "https://github.com.evil.example/x.zip"), rootHost, null, CancellationToken.None));
        Check(offSite.Contains("isn't on GitHub") && insecure.Contains("isn't on GitHub") && lookalike.Contains("isn't on GitHub") && fake.Calls == 0,
              "a download from anywhere but GitHub over https is refused before anything is fetched");
        Check(ReleaseInstaller.IsAllowedUrl("https://github.com/a/b/releases/download/v1/x.zip") && ReleaseInstaller.IsAllowedUrl("https://release-assets.githubusercontent.com/x") &&
              !ReleaseInstaller.IsAllowedUrl("https://notgithub.com/x") && !ReleaseInstaller.IsAllowedUrl("ftp://github.com/x") && !ReleaseInstaller.IsAllowedUrl("not a url"),
              "only github.com and githubusercontent.com over https are allowed");
        var noAsset = new EmpireRelease { Tag = "v0.3.0", Version = "0.3.0", Title = "Empire 0.3.0" };
        Check(InstallError(() => ReleaseInstaller.InstallAsync(http, noAsset, NewRoot(), null, CancellationToken.None)).Contains("no download for this computer"),
              "a release with no zip for this computer says so");
    }

    // Cancelling in the middle of a download stops it and leaves nothing.
    byte[] big = MakeZip(("Empire/0.1.1/empire.dll", new byte[3_000_000], Plain));
    string rootCancel = NewRoot();
    using (var http = Serving(big, out _))
    using (var cts = new CancellationTokenSource())
    {
        bool cancelled = false;
        try
        {
            await ReleaseInstaller.InstallAsync(http, Release("0.1.1", big, null), rootCancel, new SyncProgress(p => { if (p.Stage == "Downloading" && p.Fraction > 0.2) cts.Cancel(); }), cts.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && Leftovers(rootCancel).Length == 0 && !Directory.Exists(Path.Combine(rootCancel, "0.1.1")),
              "cancelling a download stops it and leaves no files behind");
    }

    // --- uninstalling an editor ---
    string uninstallRoot = NewRoot();
    string v1 = Path.Combine(uninstallRoot, "0.1.0"), v2 = Path.Combine(uninstallRoot, "0.1.1");
    foreach (var v in new[] { v1, v2 }) { Directory.CreateDirectory(Path.Combine(v, "Player")); File.WriteAllText(Path.Combine(v, "empire.dll"), "dll"); File.WriteAllText(Path.Combine(v, "Player", "Player"), "p"); }
    ReleaseInstaller.Uninstall(uninstallRoot, v1);
    Check(!Directory.Exists(v1) && File.Exists(Path.Combine(v2, "empire.dll")), "uninstalling deletes that version's folder and no other");
    string outside = Path.Combine(dir, "outside-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "empire.dll"), "x");
    string noEditorFolder = Path.Combine(uninstallRoot, "notes"); Directory.CreateDirectory(noEditorFolder); File.WriteAllText(Path.Combine(noEditorFolder, "readme.txt"), "x");
    string[] refused =
    [
        InstallError(() => { ReleaseInstaller.Uninstall(uninstallRoot, outside); return Task.CompletedTask; }),                        // not in the installs folder
        InstallError(() => { ReleaseInstaller.Uninstall(uninstallRoot, uninstallRoot); return Task.CompletedTask; }),                  // the installs folder itself
        InstallError(() => { ReleaseInstaller.Uninstall(uninstallRoot, Path.Combine(v2, "Player")); return Task.CompletedTask; }),     // deeper than a version folder
        InstallError(() => { ReleaseInstaller.Uninstall(uninstallRoot, Path.Combine(v2, "..", "..")); return Task.CompletedTask; }),   // sneaking out with ..
        InstallError(() => { ReleaseInstaller.Uninstall(uninstallRoot, noEditorFolder); return Task.CompletedTask; }),                      // no editor in it
    ];
    Check(refused.All(m => m.Length > 0) && Directory.Exists(outside) && Directory.Exists(v2) && Directory.Exists(noEditorFolder) && Directory.Exists(uninstallRoot),
          "uninstall refuses a folder outside the installs folder, the installs folder itself, a subfolder, a .. path and a folder with no editor");
    if (!OperatingSystem.IsWindows())
    {
        string linked = Path.Combine(uninstallRoot, "0.9.0");
        Directory.CreateSymbolicLink(linked, outside);
        ReleaseInstaller.Uninstall(uninstallRoot, linked);
        Check(!Directory.Exists(linked) && File.Exists(Path.Combine(outside, "empire.dll")), "uninstalling a link removes the link and never what it points at");
    }

    // --- updating the hub itself ---
    string hubJson = """
        [
          { "tag_name": "v0.3.0", "draft": false, "prerelease": true, "assets": [ { "name": "EmpireEngineHub-0.3.0-win-x64.zip", "size": 1, "state": "uploaded", "browser_download_url": "https://github.com/x/h30" } ] },
          { "tag_name": "v0.2.0", "draft": false, "prerelease": false, "assets": [
              { "name": "EmpireEngineHub-0.2.0-win-x64.zip", "size": 10, "state": "uploaded", "browser_download_url": "https://github.com/x/EmpireEngineHub-0.2.0-win-x64.zip", "digest": "sha256:AB12" },
              { "name": "EmpireEngine-0.2.0-win-x64.zip", "size": 99, "state": "uploaded", "browser_download_url": "https://github.com/x/EmpireEngine-0.2.0-win-x64.zip" } ] },
          { "tag_name": "v0.1.5", "draft": false, "prerelease": false, "assets": [ { "name": "EmpireEngine-0.1.5-win-x64.zip", "size": 1, "state": "uploaded", "browser_download_url": "https://github.com/x/e15" } ] },
          { "tag_name": "v0.1.1", "draft": false, "prerelease": false, "assets": [ { "name": "EmpireHub-0.1.1-win-x64.zip", "size": 1, "state": "uploaded", "browser_download_url": "https://github.com/x/h11" } ] }
        ]
        """;
    var hubReleases = ReleaseCatalog.Parse(hubJson, "win-x64");
    Check(hubReleases.Single(r => r.Version == "0.2.0") is { HubAsset.Name: "EmpireEngineHub-0.2.0-win-x64.zip", Asset.Name: "EmpireEngine-0.2.0-win-x64.zip" } &&
          hubReleases.Single(r => r.Version == "0.1.1").HubAsset!.Name == "EmpireHub-0.1.1-win-x64.zip" && hubReleases.Single(r => r.Version == "0.1.1").Asset is null,
          "a release's Hub zip and editor zip are told apart, and the 0.1.0-style EmpireHub- name still counts as a Hub zip");
    Check(HubUpdater.FindUpdate(hubReleases, "0.1.0")?.Version == "0.2.0" && HubUpdater.FindUpdate(hubReleases, "0.2.0") is null && HubUpdater.FindUpdate(hubReleases, "0.9.0") is null &&
          HubUpdater.FindUpdate(hubReleases, "0.1.5")?.Version == "0.2.0",
          "an update is the newest final release with a Hub zip that is newer than this Hub: a pre-release, an older one and the same version are not");
    Check(HubUpdater.FindUpdate(ReleaseCatalog.Parse(hubJson, "linux-x64"), "0.1.0") is null, "no update is offered when the newer release has no Hub zip for this computer");

    byte[] hubZip = MakeZip(("EmpireHub", Bytes("host"), Plain), ("EmpireHub.dll", Bytes("dll"), Plain), ("Licenses/NOTICE.txt", Bytes("n"), Plain),
        ("Empire/0.2.0/empire.dll", Bytes("must not be unpacked"), Plain));
    HubUpdate Update(byte[] zip, string? sha, string url = "https://github.com/ardenarrison/EmpireEngine/releases/download/v0.2.0/EmpireEngineHub-0.2.0-osx-arm64.zip") =>
        new("0.2.0", "v0.2.0", new ReleaseAsset("EmpireEngineHub-0.2.0-osx-arm64.zip", url, zip.Length, sha), null, "notes");
    string TempRoot() => Path.Combine(dir, "tmp-" + Guid.NewGuid().ToString("N"));
    string[] UpdateLeftovers(string root) => Directory.Exists(root) ? Directory.GetFileSystemEntries(root) : [];

    string updTemp = TempRoot();
    using (var http = Serving(hubZip, out _))
    {
        var reportsU = new List<InstallProgress>();
        string staged = await HubUpdater.DownloadAsync(http, Update(hubZip, Sha(hubZip)), new SyncProgress(reportsU.Add), CancellationToken.None, updTemp);
        Check(File.Exists(Path.Combine(staged, HubUpdater.ExecutableName)) && File.ReadAllText(Path.Combine(staged, "Licenses", "NOTICE.txt")) == "n" &&
              !Directory.Exists(Path.Combine(staged, "Empire")) && !File.Exists(Path.Combine(Path.GetDirectoryName(staged)!, "download.zip")),
              "a Hub update is downloaded, checked and unpacked, without the Empire/ folder and without leaving the zip behind");
        Check(reportsU.Any(r => r.Stage == "Downloading") && reportsU.Any(r => r.Stage == "Unpacking"), "an update reports its progress");
        if (!OperatingSystem.IsWindows())
            Check((File.GetUnixFileMode(Path.Combine(staged, HubUpdater.ExecutableName)) & UnixFileMode.UserExecute) != 0, "the new Hub program can be run");
    }
    string UpdateError(HttpClient http, HubUpdate update, string root)
    {
        try { HubUpdater.DownloadAsync(http, update, null, CancellationToken.None, root).GetAwaiter().GetResult(); return ""; }
        catch (InstallException ex) { return ex.Message; }
    }
    string updBad = TempRoot();
    using (var http = Serving(hubZip, out _))
    {
        string wrongSumU = UpdateError(http, Update(hubZip, new string('0', 64)), updBad);
        string noSumU = UpdateError(http, Update(hubZip, null), updBad);
        string offSiteU = UpdateError(http, Update(hubZip, Sha(hubZip), "https://evil.example.com/hub.zip"), updBad);
        byte[] notHub = MakeZip(("readme.txt", Bytes("x"), Plain));
        using var http2 = Serving(notHub, out _);
        string notHubU = UpdateError(http2, Update(notHub, Sha(notHub)), updBad);
        byte[] slipHub = MakeZip(("EmpireHub", Bytes("h"), Plain), ("../evil.txt", Bytes("pwned"), Plain));
        using var http3 = Serving(slipHub, out _);
        string slipU = UpdateError(http3, Update(slipHub, Sha(slipHub)), updBad);
        Check(wrongSumU.Contains("checksum") && noSumU.Contains("no checksum") && offSiteU.Contains("isn't on GitHub") && notHubU.Contains("doesn't contain the Hub") && slipU.Contains("unsafe path"),
              "an update with a wrong checksum, no checksum, an address off GitHub, no Hub in it or an unsafe path is refused");
        Check(UpdateLeftovers(updBad).Length == 0 && !File.Exists(Path.Combine(updBad, "..", "evil.txt")), "a refused update leaves nothing behind");
    }

    // Swapping the files: the new Hub's files replace the old ones, the editors in Empire/ and the user's other files stay.
    string newHub = Path.Combine(dir, "newhub-" + Guid.NewGuid().ToString("N")), oldHub = Path.Combine(dir, "oldhub-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(newHub, "Licenses")); Directory.CreateDirectory(Path.Combine(oldHub, "Empire", "0.1.1")); Directory.CreateDirectory(Path.Combine(oldHub, "Licenses"));
    File.WriteAllText(Path.Combine(newHub, HubUpdater.ExecutableName), "new host"); File.WriteAllText(Path.Combine(newHub, "EmpireHub.dll"), "new dll");
    File.WriteAllText(Path.Combine(newHub, "Licenses", "NOTICE.txt"), "new notice"); Directory.CreateDirectory(Path.Combine(newHub, "Empire")); File.WriteAllText(Path.Combine(newHub, "Empire", "intruder.txt"), "x");
    File.WriteAllText(Path.Combine(oldHub, HubUpdater.ExecutableName), "old host"); File.WriteAllText(Path.Combine(oldHub, "EmpireHub.dll"), "old dll");
    File.WriteAllText(Path.Combine(oldHub, "Licenses", "OLD.txt"), "old only"); File.WriteAllText(Path.Combine(oldHub, "Empire", "0.1.1", "empire.dll"), "editor");
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(newHub, HubUpdater.ExecutableName), (UnixFileMode)0x1ED);
    HubUpdater.CopyOver(newHub, oldHub);
    Check(File.ReadAllText(Path.Combine(oldHub, "EmpireHub.dll")) == "new dll" && File.ReadAllText(Path.Combine(oldHub, HubUpdater.ExecutableName)) == "new host" &&
          File.ReadAllText(Path.Combine(oldHub, "Licenses", "NOTICE.txt")) == "new notice" && File.Exists(Path.Combine(oldHub, "Licenses", "OLD.txt")),
          "applying an update replaces the Hub's files and keeps files only the old Hub had");
    Check(File.ReadAllText(Path.Combine(oldHub, "Empire", "0.1.1", "empire.dll")) == "editor" && !File.Exists(Path.Combine(oldHub, "Empire", "intruder.txt")) &&
          !Directory.GetFiles(oldHub, "*.new-*", SearchOption.AllDirectories).Any(),
          "applying an update never touches the installed editors in Empire/, and leaves no temporary files");
    if (!OperatingSystem.IsWindows())
        Check((File.GetUnixFileMode(Path.Combine(oldHub, HubUpdater.ExecutableName)) & UnixFileMode.UserExecute) != 0, "the replaced Hub program keeps its executable permission");

    // Whether a Hub may replace itself.
    string installedHub = Path.Combine(dir, "installed-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(installedHub);
    string buildHub = Path.Combine(dir, "build-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(buildHub); File.WriteAllText(Path.Combine(buildHub, "EmpireHub.pdb"), "");
    Check(HubUpdater.CanReplace(installedHub, Path.Combine(installedHub, HubUpdater.ExecutableName), out _) &&
          !HubUpdater.CanReplace(installedHub, "/usr/bin/dotnet", out string why1) && why1.Contains("not from an installed Hub") &&
          !HubUpdater.CanReplace(installedHub, null, out _) && !HubUpdater.CanReplace(buildHub, Path.Combine(buildHub, HubUpdater.ExecutableName), out _) &&
          !HubUpdater.CanReplace(installedHub, Path.Combine(buildHub, HubUpdater.ExecutableName), out _),
          "a Hub replaces itself only when it is the installed program in its own folder, not a build or dotnet");

    // --- the Hub as a macOS app (Empire Engine Hub.app) ---
    const string AppName = "Empire Engine Hub.app";
    Check(HubPaths.IsInAppBundle("/Applications/Empire Engine Hub.app/Contents/MacOS") && HubPaths.IsInAppBundle("/Applications/Empire Engine Hub.app/Contents/MacOS/") &&
          HubPaths.IsInAppBundle("C:\\Apps\\Hub.app\\Contents\\MacOS\\") && HubPaths.IsInAppBundle("/x/hub.APP/contents/macos") &&
          !HubPaths.IsInAppBundle("/Applications/Hub/Contents/MacOS") && !HubPaths.IsInAppBundle("/x/.app/Contents/MacOS") && !HubPaths.IsInAppBundle("/x/Foo.app/Contents/Resources") &&
          !HubPaths.IsInAppBundle("/x/Hub.app") && !HubPaths.IsInAppBundle("/srv/me-hub"),
          "a Hub folder is recognised as the inside of an app only when it is <Name>.app/Contents/MacOS");
    Check(HubPaths.BundlePath("/Applications/Empire Engine Hub.app/Contents/MacOS/") == "/Applications/Empire Engine Hub.app" && HubPaths.BundlePath("/srv/me-hub") is null,
          "the app's own folder is found from the Hub's folder inside it");
    Check(HubPaths.InstallsRootFor("/Applications/Empire Engine Hub.app/Contents/MacOS", "/data/Library/Application Support") == Path.Combine("/data/Library/Application Support", "EmpireHub", "Empire") &&
          HubPaths.InstallsRootFor("/opt/hub", "/data/AppData") == Path.Combine("/opt/hub", "Empire"),
          "an app keeps its installed editors in the user's app-data folder, not inside itself; a plain Hub keeps them beside its files");
    Check(HubUpdater.ExecutableIn("/x/" + AppName) == Path.Combine("/x", AppName, "Contents", "MacOS", "EmpireHub") && HubUpdater.ExecutableIn("/opt/hub") == Path.Combine("/opt/hub", HubUpdater.ExecutableName) &&
          HubUpdater.IsBundleRoot("/x/" + AppName) && HubUpdater.IsBundleRoot("/x/" + AppName + "/") && !HubUpdater.IsBundleRoot("/opt/hub"),
          "the Hub's program is found inside an app (Contents/MacOS) or at the top of a plain folder");

    (string, byte[], int)[] AppFiles(string app, params (string, byte[], int)[] more) => new (string, byte[], int)[]
    {
        ($"{app}/Contents/MacOS/EmpireHub", Bytes("host"), Exec), ($"{app}/Contents/MacOS/EmpireHub.dll", Bytes("dll"), Plain), ($"{app}/Contents/MacOS/libSkia.dylib", Bytes("native"), Plain),
        ($"{app}/Contents/MacOS/Licenses/NOTICE.txt", Bytes("notice"), Plain), ($"{app}/Contents/Info.plist", Bytes("plist"), Plain), ($"{app}/Contents/Resources/EmpireEngineIcon.icns", Bytes("icns"), Plain),
        ($"{app}/Contents/_CodeSignature/CodeResources", Bytes("seal"), Plain),
    }.Concat(more).ToArray();
    // a zip made by macOS's ditto also holds __MACOSX entries (the app's signature as file attributes), which must be ignored
    byte[] appZip = MakeZip(AppFiles(AppName, ("__MACOSX/" + AppName + "/Contents/MacOS/._EmpireHub", Bytes("junk"), Plain), ("__MACOSX/._" + AppName, Bytes("junk"), Plain)));
    string appStage = Path.Combine(dir, "appstage-" + Guid.NewGuid().ToString("N"));
    string appZipPath = Path.Combine(dir, "app-" + Guid.NewGuid().ToString("N") + ".zip"); File.WriteAllBytes(appZipPath, appZip);
    string appRoot = HubUpdater.Unpack(appZipPath, appStage);
    Check(appRoot == Path.Combine(appStage, AppName) && File.ReadAllText(Path.Combine(appRoot, "Contents", "Info.plist")) == "plist" && File.Exists(Path.Combine(appRoot, "Contents", "Resources", "EmpireEngineIcon.icns")) &&
          File.ReadAllText(Path.Combine(appRoot, "Contents", "MacOS", "Licenses", "NOTICE.txt")) == "notice" && !Directory.Exists(Path.Combine(appStage, "__MACOSX")),
          "a Hub update that is a macOS app is unpacked as the app, without the extra __MACOSX entries");
    if (!OperatingSystem.IsWindows())
        Check((File.GetUnixFileMode(HubUpdater.ExecutableIn(appRoot)) & UnixFileMode.UserExecute) != 0, "the app's program can be run");

    string UnpackError(byte[] zipBytes) { string zp = Path.Combine(dir, "bad-" + Guid.NewGuid().ToString("N") + ".zip"); File.WriteAllBytes(zp, zipBytes); try { HubUpdater.Unpack(zp, Path.Combine(dir, "badstage-" + Guid.NewGuid().ToString("N"))); return ""; } catch (InstallException ex) { return ex.Message; } }
    Check(UnpackError(MakeZip(AppFiles(AppName).Where(f => !f.Item1.EndsWith("EmpireHub.dll")).ToArray())).Contains("doesn't contain the Hub") &&
          UnpackError(MakeZip(AppFiles(AppName, ($"{AppName}/Contents/MacOS/../../../evil.txt", Bytes("pwned"), Plain)))).Contains("unsafe path") &&
          UnpackError(MakeZip(AppFiles("Bad<>Name.app"))).Contains("usable name") &&
          UnpackError(MakeZip(AppFiles(AppName, ($"{AppName}/Contents/MacOS/link", Bytes("/etc/passwd"), 0xA1FF)))).Contains("symbolic link"),
          "a Hub app with its program missing, an unsafe path, a strange name or a symbolic link is refused");
    Check(Directory.GetFiles(dir, "evil.txt", SearchOption.AllDirectories).Length == 0 && !File.Exists(Path.Combine(dir, "..", "evil.txt")), "a refused app writes nothing outside its folder");

    string appTemp = TempRoot();
    using (var http = Serving(appZip, out _))
    {
        string stagedApp = await HubUpdater.DownloadAsync(http, Update(appZip, Sha(appZip)), null, CancellationToken.None, appTemp);
        Check(HubUpdater.IsBundleRoot(stagedApp) && File.Exists(HubUpdater.ExecutableIn(stagedApp)), "downloading a Hub update that is an app gives the app, checked against its checksum like any other");
    }

    // putting a new app over the installed one: the result is exactly the new app
    string newApp = Path.Combine(dir, "newapp-" + Guid.NewGuid().ToString("N"), AppName), oldApp = Path.Combine(dir, "oldapp-" + Guid.NewGuid().ToString("N"), AppName);
    foreach (var (appFolder, files) in new[] { (newApp, AppFiles("x").Select(f => (f.Item1[2..], f.Item2, f.Item3)).ToArray()) })
        foreach (var (name, data, _) in files) { string target = Path.Combine(appFolder, name); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, data); }
    File.WriteAllText(Path.Combine(newApp, "Contents", "MacOS", "brand-new.txt"), "new");
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(newApp, "Contents", "MacOS", "EmpireHub"), (UnixFileMode)0x1ED);   // rwxr-xr-x, as the zip would hold it
    foreach (var (name, text) in new[] { ("Contents/MacOS/EmpireHub", "old host"), ("Contents/MacOS/EmpireHub.dll", "old dll"), ("Contents/MacOS/only-in-old.dll", "gone"), ("Contents/MacOS/OldDir/inner.txt", "gone"), ("Contents/Resources/old.icns", "gone") })
    {
        string target = Path.Combine(oldApp, name); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, text);
    }
    Check(HubUpdater.ApplyFiles(newApp, oldApp, resign: false) is null &&
          File.ReadAllText(Path.Combine(oldApp, "Contents", "MacOS", "EmpireHub.dll")) == "dll" && File.ReadAllText(Path.Combine(oldApp, "Contents", "MacOS", "brand-new.txt")) == "new" &&
          File.ReadAllText(Path.Combine(oldApp, "Contents", "Info.plist")) == "plist" && File.Exists(Path.Combine(oldApp, "Contents", "_CodeSignature", "CodeResources")),
          "applying a Hub app over the installed one brings in the new files");
    Check(!File.Exists(Path.Combine(oldApp, "Contents", "MacOS", "only-in-old.dll")) && !Directory.Exists(Path.Combine(oldApp, "Contents", "MacOS", "OldDir")) && !File.Exists(Path.Combine(oldApp, "Contents", "Resources", "old.icns")),
          "and removes what the new app doesn't have, so the result is exactly the new app");
    List<string> sameFiles(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)).Order().ToList();
    Check(sameFiles(newApp).SequenceEqual(sameFiles(oldApp)) && !Directory.GetFiles(oldApp, "*.new-*", SearchOption.AllDirectories).Any(), "the two apps hold the same files, and no temporary files are left");
    if (!OperatingSystem.IsWindows())
        Check((File.GetUnixFileMode(Path.Combine(oldApp, "Contents", "MacOS", "EmpireHub")) & UnixFileMode.UserExecute) != 0, "the app's program keeps its executable permission after the swap");

    string notAnApp = Path.Combine(dir, "plain-" + Guid.NewGuid().ToString("N"), "NotHub.app"); Directory.CreateDirectory(notAnApp); File.WriteAllText(Path.Combine(notAnApp, "precious.txt"), "keep me");
    string flatSource = Path.Combine(dir, "flatsrc-" + Guid.NewGuid().ToString("N")), flatTarget = Path.Combine(dir, "flattgt-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(flatSource); Directory.CreateDirectory(flatTarget); File.WriteAllText(Path.Combine(flatSource, HubUpdater.ExecutableName), "new"); File.WriteAllText(Path.Combine(flatTarget, "keep.txt"), "kept");
    Check(HubUpdater.ApplyFiles(newApp, notAnApp, resign: false) is { } notHubMessage && notHubMessage.Contains("isn't a Hub app") && File.ReadAllText(Path.Combine(notAnApp, "precious.txt")) == "keep me",
          "a folder that is not a Hub app is never rewritten, even if it is named like one");
    Check(HubUpdater.ApplyFiles(newApp, flatTarget, resign: false)?.Contains("packaged differently") == true && HubUpdater.ApplyFiles(flatSource, oldApp, resign: false)?.Contains("packaged differently") == true &&
          File.ReadAllText(Path.Combine(flatTarget, "keep.txt")) == "kept" && sameFiles(newApp).SequenceEqual(sameFiles(oldApp)),
          "an app update can't be put over a plain Hub, or the other way round: it says so and changes nothing");
    Check(HubUpdater.ApplyFiles(flatSource, flatTarget, resign: false) is null && File.ReadAllText(Path.Combine(flatTarget, HubUpdater.ExecutableName)) == "new" && File.ReadAllText(Path.Combine(flatTarget, "keep.txt")) == "kept",
          "a plain Hub is updated as before, keeping files only the old one had");

    // an app can only update itself where it can write inside itself (not when macOS opened it read-only from the Downloads folder)
    string fakeApp = Path.Combine(dir, "fakeapp-" + Guid.NewGuid().ToString("N"), AppName), fakeMacOS = Path.Combine(fakeApp, "Contents", "MacOS");
    Directory.CreateDirectory(fakeMacOS);
    Check(HubUpdater.CanReplace(fakeMacOS, Path.Combine(fakeMacOS, HubUpdater.ExecutableName), out _), "an app in a place it can write can update itself");
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(fakeMacOS, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        bool canWrite = HubUpdater.CanReplace(fakeMacOS, Path.Combine(fakeMacOS, HubUpdater.ExecutableName), out string readOnlyReason);
        File.SetUnixFileMode(fakeMacOS, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Check(!canWrite && readOnlyReason.Contains("Applications"), "an app that can't write inside itself says to move it to Applications, instead of failing");
    }

    // --- the editor as a macOS app (Empire Engine Editor.app inside the version folder) ---
    const string EditorApp = "Empire Engine Editor.app";
    (string, byte[], int)[] EditorAppFiles(string version, params (string, byte[], int)[] more) => new (string, byte[], int)[]
    {
        ($"Empire/{version}/{EditorApp}/Contents/MacOS/empire", Bytes("#!/bin/sh\necho started \"$@\" > \"$PWD/launched.txt\"\n"), Exec),
        ($"Empire/{version}/{EditorApp}/Contents/MacOS/empire.dll", Bytes("dll"), Plain), ($"Empire/{version}/{EditorApp}/Contents/MacOS/Player/Player", Bytes("player"), Plain),
        ($"Empire/{version}/{EditorApp}/Contents/MacOS/Licenses/ThirdParty/x.txt", Bytes("notice"), Plain), ($"Empire/{version}/{EditorApp}/Contents/Info.plist", Bytes("plist"), Plain),
        ($"Empire/{version}/{EditorApp}/Contents/Resources/EmpireEngineIcon.icns", Bytes("icns"), Plain), ($"Empire/{version}/{EditorApp}/Contents/_CodeSignature/CodeResources", Bytes("seal"), Plain),
    }.Concat(more).ToArray();
    byte[] editorAppZip = MakeZip(EditorAppFiles("0.2.5", ("__MACOSX/Empire/0.2.5/" + EditorApp + "/Contents/MacOS/._empire.dll", Bytes("junk"), Plain), ("Empire/0.0.1/empire.dll", Bytes("older plain editor"), Plain)));

    string editorStage = Path.Combine(dir, "editorstage-" + Guid.NewGuid().ToString("N")), editorZipPath = Path.Combine(dir, "editorapp-" + Guid.NewGuid().ToString("N") + ".zip");
    File.WriteAllBytes(editorZipPath, editorAppZip);
    string unpackedVersion = ReleaseInstaller.Unpack(editorZipPath, "0.2.5", editorStage);
    string? stagedEntry = EditorLayout.EntryFolder(editorStage);
    Check(unpackedVersion == "0.2.5" && stagedEntry == Path.Combine(editorStage, EditorApp, "Contents", "MacOS") && EditorLayout.AppOf(stagedEntry!) == Path.Combine(editorStage, EditorApp) &&
          File.ReadAllText(Path.Combine(stagedEntry!, "Licenses", "ThirdParty", "x.txt")) == "notice" && File.Exists(Path.Combine(editorStage, EditorApp, "Contents", "Resources", "EmpireEngineIcon.icns")) &&
          !Directory.Exists(Path.Combine(editorStage, "__MACOSX")) && !File.Exists(Path.Combine(editorStage, "empire.dll")),
          "an editor that is a Mac app is unpacked as the app inside its version folder, found by its inside, without the __MACOSX entries or other versions");
    if (!OperatingSystem.IsWindows())
        Check((File.GetUnixFileMode(Path.Combine(stagedEntry!, "empire")) & UnixFileMode.UserExecute) != 0 && (File.GetUnixFileMode(Path.Combine(stagedEntry!, "Player", "Player")) & UnixFileMode.UserExecute) != 0,
              "the app's editor and player programs can be run");

    string editorAppRoot = NewRoot();
    using (var http = Serving(editorAppZip, out _))
    {
        string installedApp = await ReleaseInstaller.InstallAsync(http, Release("0.2.5", editorAppZip, Sha(editorAppZip)), editorAppRoot, null, CancellationToken.None, resign: false);
        Check(installedApp == Path.Combine(editorAppRoot, "0.2.5") && ReleaseInstaller.InstalledVersions(editorAppRoot).SetEquals(["0.2.5"]) && File.Exists(Path.Combine(installedApp, ReleaseInstaller.MarkerFile)) && Leftovers(editorAppRoot).Length == 0,
              "installing an editor that is a Mac app puts the app in <installs>/<version>/ and it counts as installed");
        var asInstall = EditorInstall.FromFolder(installedApp, bundled: true);
        Check(asInstall is { Downloaded: true, Bundled: true } && asInstall.Folder == Path.GetFullPath(installedApp) && asInstall.EntryFolder == Path.GetFullPath(Path.Combine(installedApp, EditorApp, "Contents", "MacOS")) && asInstall.Version == "0.2.5",
              "the Hub's list shows the editor app, with the version folder as its folder and the app's inside as where its files are");
        Check(EditorInstall.FromFolder(Path.Combine(installedApp, EditorApp), bundled: false)?.EntryFolder == asInstall!.EntryFolder, "pointing the Hub at the .app itself (Locate) also finds the editor");
        Check(EditorInstall.FromFolder(editorAppRoot, bundled: false) is null, "a folder that only holds installs, not an editor, isn't one");
        if (!OperatingSystem.IsWindows())
        {
            string project = Path.Combine(dir, "launch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(project);
            using var launched = asInstall.Launch(Path.Combine(project, "Scenes", "Main.empirescene"), project);
            launched.WaitForExit(5000);
            Check(File.Exists(Path.Combine(project, "launched.txt")) && File.ReadAllText(Path.Combine(project, "launched.txt")).Contains("Main.empirescene") && File.ReadAllText(Path.Combine(project, "launched.txt")).Contains(project),
                  "the Hub starts the editor from inside its app, in the project's folder, given the scene and the project");
        }
        ReleaseInstaller.Uninstall(editorAppRoot, installedApp);
        Check(!Directory.Exists(installedApp) && Directory.Exists(editorAppRoot), "uninstalling an editor app deletes its version folder");
    }
    var plainAndApp = NewRoot();
    foreach (var (name, entryFile) in new[] { ("0.3.0", "empire.dll"), ("0.3.1", $"{EditorApp}/Contents/MacOS/empire.dll"), ("0.3.2", "nothing.txt") })
    {
        string file = Path.Combine(plainAndApp, name, entryFile); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "x");
    }
    Check(ReleaseInstaller.InstalledVersions(plainAndApp).SetEquals(["0.3.0", "0.3.1"]), "a plain editor folder and an editor app both count as installed, and a folder with neither doesn't");
    string emptyVersionFolder = Path.Combine(plainAndApp, "0.3.2");
    Check(InstallError(() => { ReleaseInstaller.Uninstall(plainAndApp, emptyVersionFolder); return Task.CompletedTask; }).Length > 0 && Directory.Exists(emptyVersionFolder), "uninstall still refuses a folder that holds no editor");
    byte[] brokenEditorApp = MakeZip(EditorAppFiles("0.4.0").Where(f => !f.Item1.EndsWith("empire.dll")).Append(($"Empire/0.4.0/{EditorApp}/Contents/MacOS/other.dll", Bytes("x"), Plain)).ToArray());
    string brokenZip = Path.Combine(dir, "brokenapp-" + Guid.NewGuid().ToString("N") + ".zip"); File.WriteAllBytes(brokenZip, brokenEditorApp);
    Check(InstallError(() => { ReleaseInstaller.Unpack(brokenZip, "0.4.0", Path.Combine(dir, "brokenstage")); return Task.CompletedTask; }).Contains("doesn't contain an Empire editor"), "an editor app without its empire.dll is refused");
    byte[] slipEditorApp = MakeZip(EditorAppFiles("0.4.1", ($"Empire/0.4.1/{EditorApp}/Contents/MacOS/../../../../evil.txt", Bytes("pwned"), Plain)));
    string slipZip = Path.Combine(dir, "slipapp-" + Guid.NewGuid().ToString("N") + ".zip"); File.WriteAllBytes(slipZip, slipEditorApp);
    Check(InstallError(() => { ReleaseInstaller.Unpack(slipZip, "0.4.1", Path.Combine(dir, "slipstage")); return Task.CompletedTask; }).Contains("unsafe path"), "an editor app with an unsafe path is refused");

    // Asking GitHub for an update end to end (a fake GitHub).
    using (var http = new HttpClient(new FakeGitHub(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(hubJson), RequestMessage = r })))
        Check((await HubUpdater.CheckAsync(http, CancellationToken.None, "0.1.0", "win-x64"))?.Version == "0.2.0" && await HubUpdater.CheckAsync(http, CancellationToken.None, "0.2.0", "win-x64") is null,
              "checking for an update finds the newer Hub, and says nothing when this Hub is the newest");

    // Old update folders are cleaned up, new ones kept.
    string cleanRoot = TempRoot(); Directory.CreateDirectory(cleanRoot);
    string oldUpd = Path.Combine(cleanRoot, "EmpireHubUpdate-old"), newUpd = Path.Combine(cleanRoot, "EmpireHubUpdate-new"), other = Path.Combine(cleanRoot, "something-else");
    foreach (var d in new[] { oldUpd, newUpd, other }) Directory.CreateDirectory(d);
    Directory.SetCreationTimeUtc(oldUpd, DateTime.UtcNow.AddHours(-3));
    HubUpdater.CleanOldUpdates(cleanRoot);
    Check(!Directory.Exists(oldUpd) && Directory.Exists(newUpd) && Directory.Exists(other), "leftover update folders older than an hour are deleted, and nothing else is");

    // --- choosing an editor version for a project ---
    Check(EditorVersions.Suggest("0.1.0", ["0.1.1", "0.2.0"]) == "0.1.1" && EditorVersions.Suggest("0.1.5", ["0.1.1", "0.2.0", "0.3.0"]) == "0.2.0" &&
          EditorVersions.Suggest("0.5.0", ["0.1.1", "0.2.0"]) == "0.2.0" && EditorVersions.Suggest("0.2.0", ["0.2.0-beta", "0.10.0", "0.9.0"]) == "0.2.0-beta",
          "the suggested editor is the lowest installed one that is at least as new as the project's, else the newest (compared by number, so 0.10 is newer than 0.9)");
    Check(EditorVersions.Suggest("0.1.0", Array.Empty<string>()) is null, "nothing is suggested when no editor is installed");

    string projDir = Path.Combine(dir, "proj-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(projDir);
    File.WriteAllText(Path.Combine(projDir, "project.empire"), """{ "name": "Space", "editorVersion": "0.1.0", "startScene": "Scenes/Level1.empirescene" }""");
    var changed = EmpireProject.SetEditorVersion(projDir, "0.1.1");
    var reread = EmpireProject.TryRead(projDir)!;
    Check(changed.EditorVersion == "0.1.1" && reread is { Name: "Space", EditorVersion: "0.1.1", StartScene: "Scenes/Level1.empirescene" },
          "changing a project's editor version keeps its name and start scene");
    EmpireProject.SetEditorVersion(projDir, null);
    Check(EmpireProject.TryRead(projDir)!.EditorVersion is null, "a project can be set back to any version");

    string bareDir = Path.Combine(dir, "bare-" + Guid.NewGuid().ToString("N") + "-My Game");
    Directory.CreateDirectory(bareDir);
    EmpireProject.SetEditorVersion(bareDir, "0.2.0");
    Check(EmpireProject.TryRead(bareDir) is { EditorVersion: "0.2.0" } made && made.Name.EndsWith("-My Game"), "a project folder with no project.empire gets one, named after the folder");

    string brokenDir = Path.Combine(dir, "broken-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(brokenDir);
    File.WriteAllText(Path.Combine(brokenDir, "project.empire"), "{ this is not json");
    bool refusedBroken = false;
    try { EmpireProject.SetEditorVersion(brokenDir, "0.1.1"); } catch (InvalidDataException) { refusedBroken = true; }
    Check(refusedBroken && File.ReadAllText(Path.Combine(brokenDir, "project.empire")) == "{ this is not json", "a project.empire that can't be read is left alone, not overwritten");
}

// ---- The editor's AI assistant: the Claude API client, key storage and prompts -----------------------------------------
{
    const string Key = "sk-" + "ant-test-0123456789abcdefghijklmnop";   // split so the repo scan doesn't mistake a fixture for a real key
    string ReplyJson(string text = "Hello there", int input = 12, int output = 7, string stop = "end_turn") =>
        $$"""{ "id": "msg_1", "type": "message", "role": "assistant", "content": [ { "type": "text", "text": "{{text}}" } ], "stop_reason": "{{stop}}", "usage": { "input_tokens": {{input}}, "output_tokens": {{output}} } }""";
    HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    async Task<(AssistantReply? Reply, string Error)> Ask(Func<HttpRequestMessage, HttpResponseMessage> respond, string key = Key, string endpoint = AssistantClient.Endpoint,
        IReadOnlyList<ChatMessage>? messages = null, CancellationToken ct = default)
    {
        using var http = new HttpClient(new FakeGitHub(respond));
        try { return (await AssistantClient.SendAsync(http, key, "claude-sonnet-5-5", "be brief", messages ?? [new ChatMessage("user", "hi")], 1000, ct, endpoint), ""); }
        catch (AssistantException ex) { return (null, ex.Message); }
    }

    // --- what is sent ---
    string? sentBody = null; HttpRequestMessage? sentRequest = null;
    var ok = await Ask(r => { sentRequest = r; sentBody = r.Content!.ReadAsStringAsync().Result; return Json(HttpStatusCode.OK, ReplyJson()); },
        messages: [new ChatMessage("user", "first"), new ChatMessage("assistant", "answer"), new ChatMessage("user", "second \"quoted\"")]);
    using (var sent = System.Text.Json.JsonDocument.Parse(sentBody!))
    {
        var body0 = sent.RootElement;
        Check(sentRequest!.Method == HttpMethod.Post && sentRequest.RequestUri!.ToString() == "https://api.anthropic.com/v1/messages" &&
              sentRequest.Headers.GetValues("x-api-key").Single() == Key && sentRequest.Headers.GetValues("anthropic-version").Single() == "2023-06-01",
              "a question goes to api.anthropic.com with the user's key and the API version in headers");
        Check(body0.GetProperty("model").GetString() == "claude-sonnet-5-5" && body0.GetProperty("max_tokens").GetInt32() == 1000 && body0.GetProperty("system").GetString() == "be brief" &&
              body0.GetProperty("messages").GetArrayLength() == 3 && body0.GetProperty("messages")[2].GetProperty("content").GetString() == "second \"quoted\"" &&
              body0.GetProperty("messages")[1].GetProperty("role").GetString() == "assistant",
              "the request holds the model, the system prompt and the whole conversation, with quotes escaped");
        Check(!sentBody!.Contains(Key), "the API key is in a header only, never in the body that is sent");
    }
    Check(ok.Reply is { Text: "Hello there", InputTokens: 12, OutputTokens: 7, StopReason: "end_turn" }, "the answer's text and token counts are read");
    var multi = AssistantClient.ParseReply("""{ "content": [ { "type": "text", "text": "A" }, { "type": "tool_use", "id": "x" }, { "type": "text", "text": "B" } ], "usage": { "input_tokens": 1, "output_tokens": 2 } }""");
    Check(multi.Text == "AB" && AssistantClient.ParseReply("""{ "content": [], "stop_reason": "refusal" }""").Text.Contains("declined") && AssistantClient.ParseReply("""{ "content": [] }""").Text.Contains("empty"),
          "several text blocks are joined, other block types are ignored, and an empty or refused answer is explained");

    // --- failures are explained, and the key never appears in a message ---
    async Task<string> ErrorFor(HttpStatusCode code, string body) => (await Ask(_ => Json(code, body))).Error;
    string e401 = await ErrorFor(HttpStatusCode.Unauthorized, """{ "type": "error", "error": { "type": "authentication_error", "message": "invalid x-api-key" } }""");
    string e429 = await ErrorFor((HttpStatusCode)429, """{ "error": { "message": "rate limited" } }""");
    string e529 = await ErrorFor((HttpStatusCode)529, "overloaded");
    string e400 = await ErrorFor(HttpStatusCode.BadRequest, """{ "error": { "message": "Your credit balance is too low to access the Anthropic API." } }""");
    string e404 = await ErrorFor(HttpStatusCode.NotFound, """{ "error": { "message": "model: nope" } }""");
    string eEcho = await ErrorFor(HttpStatusCode.Unauthorized, $$"""{ "error": { "message": "bad key {{Key}} sent" } }""");
    Check(e401.Contains("rejected the API key") && e429.Contains("too many requests") && e529.Contains("busy") && e400.Contains("credit balance is too low") && e404.Contains("model name"),
          "a rejected key, a rate limit, an overloaded service, a low balance and an unknown model each get a plain message");
    Check(!eEcho.Contains(Key) && eEcho.Contains("[key removed]"), "an error message that echoes the key has it removed before it is shown");
    var offline = await Ask(_ => throw new HttpRequestException("no route to host"));
    Check(offline.Error.Contains("Couldn't reach Anthropic") && !offline.Error.Contains(Key), "no internet connection is explained");
    var garbage = await Ask(_ => Json(HttpStatusCode.OK, "this is not json"));
    Check(garbage.Error.Contains("expected form"), "an answer that isn't JSON is a readable error");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        bool stopped = false;
        try { await Ask(_ => Json(HttpStatusCode.OK, ReplyJson()), ct: cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "stopping a question cancels it");
    }

    // --- where it may send ---
    Check(AssistantClient.IsAllowedEndpoint("https://api.anthropic.com/v1/messages") && AssistantClient.IsAllowedEndpoint("http://127.0.0.1:8080/v1/messages") &&
          !AssistantClient.IsAllowedEndpoint("http://api.anthropic.com/v1/messages") && !AssistantClient.IsAllowedEndpoint("https://evil.example.com/v1/messages") &&
          !AssistantClient.IsAllowedEndpoint("https://api.anthropic.com.evil.example/v1/messages") && !AssistantClient.IsAllowedEndpoint("not a url"),
          "the key is only ever sent to https://api.anthropic.com (this computer's own address is allowed for tests)");
    int calls = 0;
    var blocked = await Ask(_ => { calls++; return Json(HttpStatusCode.OK, ReplyJson()); }, endpoint: "https://evil.example.com/v1/messages");
    Check(blocked.Error.Contains("only talks to api.anthropic.com") && calls == 0, "a request to any other address is refused before anything is sent");
    var noMessage = await Ask(_ => { calls++; return Json(HttpStatusCode.OK, ReplyJson()); }, messages: []);
    var endsWithReply = await Ask(_ => { calls++; return Json(HttpStatusCode.OK, ReplyJson()); }, messages: [new ChatMessage("user", "a"), new ChatMessage("assistant", "b")]);
    var badKey = await Ask(_ => { calls++; return Json(HttpStatusCode.OK, ReplyJson()); }, key: "short");
    Check(noMessage.Error.Length > 0 && endsWithReply.Error.Length > 0 && badKey.Error.Contains("API key") && calls == 0, "nothing is sent without a question or with an invalid key");

    // --- the key and settings on disk ---
    string assistantDir = Path.Combine(dir, "assistant-" + Guid.NewGuid().ToString("N"));
    Check(AssistantKeyStore.ValidateKey(Key) is null && AssistantKeyStore.ValidateKey("") is not null && AssistantKeyStore.ValidateKey("has a space 0123456789abcdef") is not null &&
          AssistantKeyStore.ValidateKey("line\nbreak0123456789abcdefgh") is not null && AssistantKeyStore.ValidateKey(new string('k', 500)) is not null,
          "a key must be one printable string without spaces or line breaks, so it can't break out of a header");
    const string EnvKey = "sk-" + "ant-from-environment-0123456789abcdef";
    string? oldEnvironment = Environment.GetEnvironmentVariable(AssistantKeyStore.EnvironmentVariable);
    try
    {
        Environment.SetEnvironmentVariable(AssistantKeyStore.EnvironmentVariable, null);
        Check(AssistantKeyStore.Resolve(out _, assistantDir) is null, "with no saved key and no environment variable there is no key");
        AssistantKeyStore.Save("  " + Key + "\n", assistantDir);
        Check(AssistantKeyStore.Resolve(out string source, assistantDir) == Key && source == "file", "a saved key is read back, trimmed");
        if (!OperatingSystem.IsWindows())
            Check(File.GetUnixFileMode(Path.Combine(assistantDir, "anthropic-api-key")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "the key file can be read only by its owner");
        Environment.SetEnvironmentVariable(AssistantKeyStore.EnvironmentVariable, EnvKey);
        Check(AssistantKeyStore.Resolve(out string source2, assistantDir) == EnvKey && source2 == "environment", "the ANTHROPIC_API_KEY environment variable wins over a saved key");
        Environment.SetEnvironmentVariable(AssistantKeyStore.EnvironmentVariable, null);
        AssistantKeyStore.Delete(assistantDir);
        Check(AssistantKeyStore.Resolve(out _, assistantDir) is null && !File.Exists(Path.Combine(assistantDir, "anthropic-api-key")), "removing the saved key deletes its file");
        bool refusedSave = false;
        try { AssistantKeyStore.Save("not a key", assistantDir); } catch (AssistantException) { refusedSave = true; }
        Check(refusedSave && !File.Exists(Path.Combine(assistantDir, "anthropic-api-key")), "an invalid key is not saved");
    }
    finally { Environment.SetEnvironmentVariable(AssistantKeyStore.EnvironmentVariable, oldEnvironment); }

    var fresh = AssistantSettings.Load(assistantDir);
    Check(!fresh.HasConsented && !fresh.IncludeScript && !fresh.IncludeSelection && fresh.Model == AssistantClient.DefaultModel,
          "the assistant starts switched off, with nothing attached by default");
    fresh.ConsentVersion = AssistantSettings.CurrentConsentVersion; fresh.IncludeScript = true; fresh.Model = "claude-opus-5-5";
    fresh.Save(assistantDir);
    var assistantSaved = AssistantSettings.Load(assistantDir);
    Check(assistantSaved.HasConsented && assistantSaved.IncludeScript && !assistantSaved.IncludeSelection && assistantSaved.Model == "claude-opus-5-5", "the user's agreement and choices are remembered");
    assistantSaved.ConsentVersion = AssistantSettings.CurrentConsentVersion - 1;
    Check(!assistantSaved.HasConsented, "a notice that has changed since the user agreed is asked about again");
    File.WriteAllText(Path.Combine(assistantDir, "assistant.json"), "{ broken");
    Check(!AssistantSettings.Load(assistantDir).HasConsented, "unreadable settings count as not agreed, not as a crash");
    Check(!File.ReadAllText(Path.Combine(assistantDir, "assistant.json")).Contains("sk-ant"), "the settings file never holds the key");

    // --- what is attached ---
    var ctx = new AssistantContext("Mover", "public class Mover : Behaviour { }", "Player", "Name: Player\nPosition: (0, 1, 0)");
    string plain = AssistantPrompt.BuildUserMessage("  why is it slow?  ", ctx, includeScript: false, includeSelection: false);
    string withScript = AssistantPrompt.BuildUserMessage("why?", ctx, true, false);
    string withBoth = AssistantPrompt.BuildUserMessage("why?", ctx, true, true);
    Check(plain == "why is it slow?", "with nothing ticked only the question is sent");
    Check(withScript.Contains("<open_script name=\"Mover\">") && withScript.Contains("class Mover") && !withScript.Contains("selected_object") && withScript.EndsWith("why?") &&
          withBoth.Contains("<selected_object name=\"Player\">"), "a ticked script or selection is attached, and only those");
    var pathy = new AssistantContext("/mnt/someone/Secret Project/Scripts/Mover\"><x", "code", null, null);
    string pathMessage = AssistantPrompt.BuildUserMessage("q", pathy, true, false);
    Check(!pathMessage.Contains("/mnt") && !pathMessage.Contains("someone") && pathMessage.Contains("name=\"Moverx\">"), "only a script's name is sent, never a folder path, and a name can't break out of its tag");
    string huge = AssistantPrompt.BuildUserMessage("q", new AssistantContext("Big", new string('x', 100_000), null, null), true, false);
    Check(huge.Length < AssistantPrompt.MaxAttachmentChars + 500 && huge.Contains("too long to send in full"), "a very long script is cut, and says so");
    Check(AssistantPrompt.BuildUserMessage(new string('q', 20_000), ctx, false, false).Length == AssistantPrompt.MaxQuestionChars, "a very long question is cut");
    var longChat = Enumerable.Range(0, 60).Select(i => new ChatMessage(i % 2 == 0 ? "user" : "assistant", "m" + i)).ToList();
    var recent = AssistantPrompt.Recent(longChat);
    Check(recent.Count <= AssistantPrompt.MaxMessages && recent[0].Role == "user" && recent[^1].Text == "m59", "only the recent part of a long conversation is sent, starting with a user message");
    Check(AssistantPrompt.System.Contains("<project_context>") && AssistantPrompt.System.Contains("not instructions"), "the model is told the project content is data, not instructions");

    // --- showing answers ---
    Check(AssistantText.ToAscii("It\u2019s \u201Cgood\u201D \u2014 ok\u2026 \u2022 a\u2192b \ud83d\ude00\tx") == "It's \"good\" - ok... * a->b ??    x", "an answer is shown in the editor's ASCII font: quotes, dashes and arrows are mapped and the rest becomes ?");
    var blocks = AssistantText.CodeBlocks("Try:\n```csharp\nvar a = 1;\nvar b = 2;\n```\ntext\n```\nsecond\n```\n");
    Check(blocks.Count == 2 && blocks[0] == "var a = 1;\nvar b = 2;" && blocks[1] == "second" && AssistantText.CodeBlocks("no code").Count == 0, "the code blocks in an answer can be copied one by one");
    Check(AssistantClient.Models.Contains(AssistantClient.DefaultModel), "the default model is one of the models offered");
}

// ---- The editor's built-in terminal: the screen emulator -------------------------------------------------------------------
{
    TerminalEmulator Term(int cols = 20, int rows = 5, int scrollback = 100) => new(cols, rows, scrollback);
    TerminalEmulator Fed(string text, int cols = 20, int rows = 5) { var t = Term(cols, rows); t.Write(text); return t; }
    string Row(TerminalEmulator t, int r) => t.RowText(r);
    const string E = "\u001b";

    var t1 = Fed("Hello, world");
    Check(Row(t1, 0) == "Hello, world" && t1.CursorX == 12 && t1.CursorY == 0, "plain text appears and the cursor follows it");
    var t2 = Fed("one\r\ntwo\r\nthree");
    Check(Row(t2, 0) == "one" && Row(t2, 1) == "two" && Row(t2, 2) == "three" && t2.CursorY == 2, "carriage return and line feed move to the next line");
    var t3 = Fed("abcdefghij" + "XY", cols: 10);
    Check(Row(t3, 0) == "abcdefghij" && Row(t3, 1) == "XY", "text wraps at the right edge");
    var t3b = Fed("abcdefghij", cols: 10);
    Check(t3b.CursorX == 9 && t3b.CursorY == 0, "the cursor waits at the last column until the next character, instead of wrapping early");
    var t4 = Fed("abc\b\bX\tY");
    Check(Row(t4, 0) == "aXc     Y" && t4.CursorX == 9, "backspace moves back and a tab goes to the next stop of 8");

    // colors and styles
    var c1 = Fed(E + "[31mR" + E + "[0mN" + E + "[1;32mG" + E + "[42mB");
    Check(c1.GetCell(0, 0).Fg == 1 && c1.GetCell(0, 1).Fg == -1 && c1.GetCell(0, 2) is { Fg: 2, Attr: Cell.Bold } && c1.GetCell(0, 3).Bg == 2, "basic colors, reset, bold and backgrounds");
    var c2 = Fed(E + "[91mA" + E + "[104mB" + E + "[38;5;200mC" + E + "[48;5;17mD" + E + "[38;2;10;20;30mE" + E + "[39;49mF");
    Check(c2.GetCell(0, 0).Fg == 9 && c2.GetCell(0, 1).Bg == 12 && c2.GetCell(0, 2).Fg == 200 && c2.GetCell(0, 3).Bg == 17 &&
          c2.GetCell(0, 4).Fg == (Cell.Rgb | (10 << 16) | (20 << 8) | 30) && c2.GetCell(0, 5) is { Fg: -1, Bg: -1 }, "bright, 256-color and true-color text, and the default colors");
    var c3 = Fed(E + "[38:2::1:2:3mA" + E + "[38:5:99mB");
    Check(c3.GetCell(0, 0).Fg == (Cell.Rgb | (1 << 16) | (2 << 8) | 3) && c3.GetCell(0, 1).Fg == 99, "the colon form of 256 and true color works too");
    var c4 = Fed(E + "[1;4;7mA" + E + "[22;24;27mB");
    Check(c4.GetCell(0, 0).Attr == (Cell.Bold | Cell.Underline | Cell.Inverse) && c4.GetCell(0, 1).Attr == 0, "bold, underline and inverse switch on and off");

    // cursor movement and erasing
    var m1 = Fed("ab\r\ncd\r\nef" + E + "[1;2HX" + E + "[3;1HY");
    Check(Row(m1, 0) == "aX" && Row(m1, 2) == "Yf", "the cursor can be placed anywhere (row;column, starting at 1)");
    var m2 = Fed("1234567890" + E + "[3D" + E + "[2C" + E + "[A" + E + "[B" + E + "[5G", cols: 20);
    Check(m2.CursorX == 4 && m2.CursorY == 1, "cursor moves back, forward, up (stopping at the top), down and to a column");
    var e1 = Fed("hello world" + E + "[6D" + E + "[K");
    Check(Row(e1, 0) == "hello", "erase to the end of the line");
    var e2 = Fed("hello world" + E + "[6D" + E + "[1K");
    Check(Row(e2, 0) == "      world".TrimEnd(), "erase to the start of the line");
    var e3 = Fed("aaa\r\nbbb\r\nccc" + E + "[2;2H" + E + "[J");
    Check(Row(e3, 0) == "aaa" && Row(e3, 1) == "b" && Row(e3, 2) == "", "erase from the cursor to the end of the screen");
    var e4 = Fed("aaa\r\nbbb" + E + "[2J" + E + "[H");
    Check(Row(e4, 0) == "" && Row(e4, 1) == "" && e4.CursorX == 0 && e4.CursorY == 0, "erase the whole screen");
    var e5 = Fed(E + "[44m" + E + "[2K");
    Check(e5.GetCell(0, 5).Bg == 4, "erasing paints with the current background color");

    // scrolling
    var s1 = Fed("1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7", rows: 5);
    Check(Row(s1, 0) == "3" && Row(s1, 4) == "7" && s1.ScrollbackCount == 2 && s1.GetCell(0, 0, scrollOffset: 2).Ch == '1', "text scrolls up and the lines that leave the screen go to the scrollback");
    Check(s1.RowText(0, scrollOffset: 1) == "2" && s1.RowText(1, scrollOffset: 2) == "2", "the view can be scrolled back through them");
    var s2 = new TerminalEmulator(10, 3, scrollback: 2); s2.Write("1\r\n2\r\n3\r\n4\r\n5\r\n6\r\n7");
    Check(s2.ScrollbackCount == 2, "the scrollback is limited");
    var s3 = Fed("a\r\nb\r\nc\r\nd\r\ne" + E + "[2;4r" + E + "[4;1H\n\n", rows: 5);
    Check(Row(s3, 0) == "a" && Row(s3, 4) == "e" && Row(s3, 1) == "d" && s3.ScrollbackCount == 0, "a scrolling region scrolls only its own lines and doesn't use the scrollback");
    var s4 = Fed("a\r\nb\r\nc" + E + "[1;1H" + E + "M", rows: 3);
    Check(Row(s4, 0) == "" && Row(s4, 1) == "a" && Row(s4, 2) == "b", "reverse index at the top scrolls the screen down");

    // inserting and deleting
    var i1 = Fed("a\r\nb\r\nc" + E + "[2;1H" + E + "[L", rows: 5);
    Check(Row(i1, 1) == "" && Row(i1, 2) == "b" && Row(i1, 3) == "c", "insert a line");
    var i2 = Fed("a\r\nb\r\nc" + E + "[2;1H" + E + "[M", rows: 5);
    Check(Row(i2, 1) == "c" && Row(i2, 2) == "", "delete a line");
    var i3 = Fed("abcdef" + E + "[4G" + E + "[2@");
    Check(Row(i3, 0) == "abc  def", "insert characters");
    var i4 = Fed("abcdef" + E + "[2G" + E + "[2P");
    Check(Row(i4, 0) == "adef", "delete characters");
    var i5 = Fed("abcdef" + E + "[2G" + E + "[3X");
    Check(Row(i5, 0) == "a   ef", "erase characters in place");

    // the alternate screen
    var a1 = Fed("main text\r\nline2" + E + "[?1049h" + "ALT");
    Check(a1.AlternateScreen && Row(a1, 0) == "ALT" && Row(a1, 1) == "", "the alternate screen starts empty");
    a1.Write(E + "[?1049l");
    Check(!a1.AlternateScreen && Row(a1, 0) == "main text" && Row(a1, 1) == "line2" && a1.CursorY == 1 && a1.CursorX == 5, "leaving it brings back the main screen and the cursor");
    var a2 = new TerminalEmulator(10, 2, 50); a2.Write(E + "[?1049h" + "a\r\nb\r\nc\r\nd");
    Check(a2.ScrollbackCount == 0, "the alternate screen doesn't fill the scrollback");

    // saving the cursor, and modes
    var v1 = Fed("ab" + E + "7" + "\r\ncd" + E + "8" + "X");
    Check(Row(v1, 0) == "abX", "ESC 7 and ESC 8 save and restore the cursor");
    var v2 = Fed("ab" + E + "[s" + "\r\ncd" + E + "[u" + "X");
    Check(Row(v2, 0) == "abX", "CSI s and CSI u do too");
    var v3 = Term(); v3.Write(E + "[?25l");
    bool hidden = !v3.CursorVisible; v3.Write(E + "[?25h" + E + "[?2004h" + E + "[?1h");
    Check(hidden && v3.CursorVisible && v3.BracketedPaste && v3.ApplicationCursorKeys, "cursor visibility, bracketed paste and application cursor keys are tracked");
    var v4 = Fed(E + "[?7l" + "abcdefghijklmnopqrstuvwxyz", cols: 10);
    Check(Row(v4, 0) == "abcdefghiz" && Row(v4, 1) == "", "with wrapping off, text stays on one line");

    // text from other languages and symbols
    var u1 = Term(); var bytes = System.Text.Encoding.UTF8.GetBytes("h\u00E9llo \u2500\u2502 \u20AC");
    u1.Write(bytes.AsSpan(0, 3)); u1.Write(bytes.AsSpan(3));
    Check(Row(u1, 0) == "h\u00E9llo \u2500\u2502 \u20AC", "UTF-8 text is read, even when a character is split between two reads");
    var u2 = Term(); u2.Write(new byte[] { 0x41, 0xC3, 0x42, 0xFF, 0x43 });
    Check(Row(u2, 0).Contains('A') && Row(u2, 0).Contains('B') && Row(u2, 0).Contains('C') && Row(u2, 0).Contains('\uFFFD'), "broken UTF-8 shows replacement characters and carries on");

    // sequences it ignores or answers
    var o1 = Fed("a" + E + "]0;My title\u0007" + "b" + E + "]2;Other" + E + "\\" + "c");
    Check(Row(o1, 0) == "abc" && o1.Title == "Other", "window titles are read and don't show as text");
    var g1 = Fed("a" + E + "P1$r0m" + E + "\\" + "b" + E + "_Gignored" + E + "\\" + "c" + E + "[?9999h" + E + "[5 q" + E + "(B" + "d");
    Check(Row(g1, 0) == "abcd", "device control strings, unknown modes and character-set choices are skipped");
    var q = Term(); var answers = new List<string>(); q.Respond += answers.Add;
    q.Write("ab" + E + "[6n" + E + "[5n" + E + "[c" + E + "[>c" + E + "[>q" + E + "[?u");
    Check(answers.Count == 5 && answers[0] == E + "[1;3R" && answers[1] == E + "[0n" && answers[2].StartsWith(E + "[?") && answers[3].StartsWith(E + "[>") && answers[4].Contains("empire-terminal"),
          "the questions a program asks a terminal (cursor position, status, what are you) are answered, and unknown ones are not");
    var x = Fed("a\u001b[31");   // a sequence cut off mid-way, finished by the next write
    x.Write("mb"); Check(Row(x, 0) == "ab" && x.GetCell(0, 1).Fg == 1, "an escape sequence split between two writes is still understood");
    var x2 = Fed("a" + E + "[1" + E + "[31mb");   // a new ESC inside a sequence starts over
    Check(Row(x2, 0) == "ab" && x2.GetCell(0, 1).Fg == 1, "an escape character in the middle of a sequence starts a new one");
    var x3 = Fed("a\u0007\u0000b");
    Check(Row(x3, 0) == "ab", "bell and NUL are ignored");

    // resizing and reading text
    var r1 = Fed("12345\r\nabcde", cols: 10, rows: 3); r1.Resize(5, 3);
    Check(r1.Cols == 5 && Row(r1, 0) == "12345" && Row(r1, 1) == "abcde", "making the screen narrower keeps what fits");
    var r2 = Fed("1\r\n2\r\n3\r\n4", cols: 10, rows: 4); r2.Resize(10, 2);
    Check(r2.Rows == 2 && r2.CursorY == 1 && Row(r2, 1) == "4" && r2.ScrollbackCount == 2, "making it shorter pushes the top lines into the scrollback and keeps the cursor on screen");
    r2.Resize(10, 6); Check(r2.Rows == 6 && Row(r2, 0) != null, "making it taller works");
    var r3 = new TerminalEmulator(10, 3); r3.Write("x"); r3.Resize(1, 1);
    Check(r3.Cols >= 2 && r3.Rows == 1 && r3.CursorX < r3.Cols && r3.CursorY < r3.Rows, "a tiny size is clamped, never crashing");
    var sel = Fed("hello world\r\nsecond line   \r\nthird");
    Check(sel.GetText(0, 6, 0, 10) == "world" && sel.GetText(0, 0, 1, 5) == "hello world\nsecond" && sel.GetText(1, 5, 0, 6) == "world\nsecond" && sel.GetText(2, 0, 2, 19) == "third",
          "selected text is read back with line ends, trailing spaces trimmed, in either direction");
    var sb2 = Fed("1\r\n2\r\n3\r\n4\r\n5\r\n6", rows: 3);
    Check(sb2.GetText(-3, 0, 2, 5) == "1\n2\n3\n4\n5\n6" || sb2.GetText(-3, 0, 2, 5).Replace("\n", "") == "123456", "the scrollback can be read as text too");

    // a screenful of mixed output, as a program like a pager or Claude Code writes it
    var big = new TerminalEmulator(40, 10);
    big.Write(E + "[?1049h" + E + "[2J" + E + "[H" + E + "[1;36m+-- Title --+" + E + "[0m\r\n| " + E + "[32mok" + E + "[0m |\r\n");
    for (int i = 0; i < 30; i++) big.Write(E + "[" + (3 + i % 6) + ";1H" + E + "[2Kline " + i);
    big.Write(E + "[?1049l");
    Check(!big.AlternateScreen && big.ScrollbackCount == 0, "a full-screen program leaves the screen as it found it");
}

// ---- The editor's built-in terminal: a real program in a pseudo-terminal (macOS and Linux) ------------------------------------
if (!OperatingSystem.IsWindows())
{
    string ptyDir = Path.Combine(dir, "pty-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(ptyDir);
    async Task<bool> WaitFor(TerminalEmulator screen, PtySession session, Func<TerminalEmulator, bool> done, int ms = 8000)
    {
        for (int waited = 0; waited < ms; waited += 25)
        {
            var bytes = session.TakeOutput();
            if (bytes.Length > 0) screen.Write(bytes);
            if (done(screen)) return true;
            await Task.Delay(25);
        }
        return false;
    }
    string AllText(TerminalEmulator screen) => string.Join("\n", Enumerable.Range(-screen.ScrollbackCount, screen.ScrollbackCount + screen.Rows).Select(r => screen.RowText(r)));

    // a program that prints and exits
    using (var echo = PtySession.Start(ptyDir, 60, 12, ["/bin/echo", "hello from the terminal"]))
    {
        var screen = new TerminalEmulator(60, 12);
        bool saw = await WaitFor(screen, echo, s => AllText(s).Contains("hello from the terminal"));
        for (int i = 0; i < 80 && !echo.HasExited; i++) await Task.Delay(50);
        Check(saw && echo.HasExited && echo.ExitCode == 0, "a program run in the terminal prints its output and its exit code is reported");
    }

    // an interactive shell: it believes it is in a terminal of the size asked for, takes typed input, and can be resized
    using (var shell = PtySession.Start(ptyDir, 70, 15, ["/bin/sh"]))
    {
        var screen = new TerminalEmulator(70, 15);
        shell.Write("echo $((6*7))\r");
        Check(await WaitFor(screen, shell, s => AllText(s).Split('\n').Any(l => l.Trim() == "42")), "typed input reaches the shell and its answer comes back");
        shell.Write("stty size\r");
        Check(await WaitFor(screen, shell, s => AllText(s).Split('\n').Any(l => l.Trim() == "15 70")), "the program is told the terminal's size");
        shell.Write("[ -t 0 ] && [ -t 1 ] && echo is-a-terminal\r");
        Check(await WaitFor(screen, shell, s => AllText(s).Split('\n').Any(l => l.Trim() == "is-a-terminal")), "the program really sees a terminal, so colors and full-screen programs work");
        shell.Resize(100, 30);
        await Task.Delay(300);
        shell.Write("stty size\r");
        Check(await WaitFor(screen, shell, s => AllText(s).Split('\n').Any(l => l.Trim() == "30 100")), "resizing the terminal changes the size the program sees");
        shell.Write("printf '\\033[31mred\\033[0m\\n'\r");
        Check(await WaitFor(screen, shell, s => Enumerable.Range(0, s.Rows).Any(r => s.RowText(r) == "red" && s.GetCell(r, 0).Fg == 1)), "colors a program prints show up in the screen");
        shell.Write("exit 3\r");
        for (int i = 0; i < 100 && !shell.HasExited; i++) await Task.Delay(50);
        Check(shell.HasExited && shell.ExitCode == 3, "when the shell exits that is noticed, with its exit code");
    }

    // closing a terminal stops what runs in it
    bool SleepRunning() { using var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "pgrep -f 'sleep [6]1' >/dev/null" }, UseShellExecute = false })!; ps.WaitForExit(); return ps.ExitCode == 0; }
    var longRun = PtySession.Start(ptyDir, 60, 12, ["/bin/sleep", "61"]);
    for (int i = 0; i < 40 && !SleepRunning(); i++) await Task.Delay(50);
    bool wasRunning = SleepRunning();
    longRun.Dispose();
    for (int i = 0; i < 60 && SleepRunning(); i++) await Task.Delay(50);
    Check(wasRunning && longRun.HasExited && !SleepRunning(), "closing the terminal stops the program in it, with nothing left running");

    // text with quotes and spaces reaches the program unchanged (nothing is run by accident)
    using (var quoted = PtySession.Start(ptyDir, 60, 12, ["/bin/echo", "it's $HOME `x` \"q\""]))
    {
        var screen = new TerminalEmulator(60, 12);
        Check(await WaitFor(screen, quoted, s => AllText(s).Contains("it's $HOME `x` \"q\"")), "arguments with quotes and shell characters are passed as plain text");
    }
    Check(!Directory.GetFiles(Path.Combine(Path.GetTempPath(), "empire-terminal-" + Environment.UserName), "*.tty").Any(f => File.GetLastWriteTime(f) > DateTime.Now.AddMinutes(-2)),
          "the temporary file naming the terminal is removed when the terminal closes");
}

// ---- Project settings (File > Project Settings): the editor's far clip plane --------------------------------------------------
{
    string psDir = Path.Combine(dir, "ps-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(psDir);
    string psFile = Path.Combine(psDir, ProjectSettings.FileName);

    Check(ProjectSettings.Load(psDir).EditorFarClipPlane == 500f && ProjectSettings.DefaultFarClipPlane == 500f, "a project with no settings file uses the usual far clip plane of 500");
    Check(!File.Exists(psFile), "reading settings never creates the file");
    var ps = new ProjectSettings { EditorFarClipPlane = 12000f };
    ps.Save(psDir);
    Check(ProjectSettings.Load(psDir).EditorFarClipPlane == 12000f && File.Exists(psFile), "a saved far clip plane is read back");
    Check(Directory.GetFiles(psDir).Length == 1, "saving leaves no temporary files");

    Check(new ProjectSettings { EditorFarClipPlane = 1f }.EditorFarClipPlane == ProjectSettings.MinFarClipPlane &&
          new ProjectSettings { EditorFarClipPlane = -50f }.EditorFarClipPlane == ProjectSettings.MinFarClipPlane &&
          new ProjectSettings { EditorFarClipPlane = 1e9f }.EditorFarClipPlane == ProjectSettings.MaxFarClipPlane &&
          new ProjectSettings { EditorFarClipPlane = float.NaN }.EditorFarClipPlane == 500f &&
          new ProjectSettings { EditorFarClipPlane = float.PositiveInfinity }.EditorFarClipPlane == 500f,
          "a far clip plane outside the allowed range is brought into it, and a non-number becomes the default");

    File.WriteAllText(psFile, """{ "editorFarClipPlane": 99999999, "somethingFromANewerEditor": { "keep": [1, 2, 3] } }""");
    Check(ProjectSettings.Load(psDir).EditorFarClipPlane == ProjectSettings.MaxFarClipPlane, "an out-of-range value in the file is limited when read");
    var ps2 = ProjectSettings.Load(psDir); ps2.EditorFarClipPlane = 2500f; ps2.Save(psDir);
    string psText = File.ReadAllText(psFile);
    Check(ProjectSettings.Load(psDir).EditorFarClipPlane == 2500f && psText.Contains("somethingFromANewerEditor") && psText.Contains("\"keep\""), "saving keeps fields in the file that this editor doesn't know about");

    foreach (var bad in new[] { "{ broken", "", "[1,2]", "null", "{ \"editorFarClipPlane\": \"far\" }", "{ \"editorFarClipPlane\": null }" })
    {
        File.WriteAllText(psFile, bad);
        Check(ProjectSettings.Load(psDir).EditorFarClipPlane == 500f && File.ReadAllText(psFile) == bad, $"an unreadable settings file ({(bad.Length > 12 ? bad[..12] : bad)}...) gives the default and is not touched by reading");
    }
    File.WriteAllText(psFile, "{ broken");
    new ProjectSettings { EditorFarClipPlane = 800f }.Save(psDir);
    Check(ProjectSettings.Load(psDir).EditorFarClipPlane == 800f, "saving replaces a broken settings file with a good one");

    // the near clip plane
    Check(new ProjectSettings().EditorNearClipPlane == 0.1f && ProjectSettings.DefaultNearClipPlane == 0.1f, "the editor's near clip plane is 0.1 unless set");
    var nearSettings = new ProjectSettings { EditorFarClipPlane = 1000f, EditorNearClipPlane = 2f };
    nearSettings.Save(psDir);
    var nearBack = ProjectSettings.Load(psDir);
    Check(nearBack.EditorNearClipPlane == 2f && nearBack.EditorFarClipPlane == 1000f, "near and far clip planes are saved and read back together");
    Check(new ProjectSettings { EditorNearClipPlane = 0.0001f }.EditorNearClipPlane == ProjectSettings.MinNearClipPlane &&
          new ProjectSettings { EditorNearClipPlane = 5000f }.EditorNearClipPlane == ProjectSettings.MaxNearClipPlane &&
          new ProjectSettings { EditorNearClipPlane = float.NaN }.EditorNearClipPlane == 0.1f,
          "a near clip plane outside the allowed range is brought into it, and a non-number becomes the default");
    var tied = new ProjectSettings { EditorFarClipPlane = 1000f, EditorNearClipPlane = 80f };
    Check(tied.EditorNearClipPlane == 80f, "a near plane up to half the far plane is allowed");
    tied.EditorNearClipPlane = 900f;
    Check(tied.EditorNearClipPlane == 100f, "the near plane is never more than 100");
    var shortFar = new ProjectSettings { EditorFarClipPlane = 50f, EditorNearClipPlane = 90f };
    Check(shortFar.EditorNearClipPlane == 25f, "and never more than half the far plane"); 
    tied.EditorFarClipPlane = 40f;
    Check(tied.EditorNearClipPlane == 20f && tied.EditorFarClipPlane == 40f, "lowering the far plane pulls the near plane in with it");
    File.WriteAllText(psFile, """{ "editorNearClipPlane": 60, "editorFarClipPlane": 100 }""");
    Check(ProjectSettings.Load(psDir) is { EditorFarClipPlane: 100f, EditorNearClipPlane: 50f }, "a file whose near plane is too far for its far plane is read with the near plane limited");
    File.WriteAllText(psFile, """{ "editorFarClipPlane": 700 }""");
    Check(ProjectSettings.Load(psDir) is { EditorFarClipPlane: 700f, EditorNearClipPlane: 0.1f }, "a file with only a far plane keeps the usual near plane");

    bool refused = false;
    try { new ProjectSettings().Save(Path.Combine(psDir, "no-such-folder")); } catch (IOException) { refused = true; } catch (UnauthorizedAccessException) { refused = true; }
    Check(refused, "saving to a folder that doesn't exist is reported, not silently ignored");
}

// ---- The editor's light or dark look: --lightmode, --darkmode, or the system's ---------------------------------------------------
{
    var none = ThemeChoice.FromArguments(["scene.empirescene", "proj"], out var plain);
    Check(none == ThemeChoice.Request.System && plain.SequenceEqual(["scene.empirescene", "proj"]), "without a theme flag the editor asks the system, and its arguments are untouched");
    Check(ThemeChoice.FromArguments(["--lightmode", "scene.empirescene", "proj"], out var a1) == ThemeChoice.Request.Light && a1.SequenceEqual(["scene.empirescene", "proj"]) &&
          ThemeChoice.FromArguments(["scene.empirescene", "proj", "--darkmode"], out var a2) == ThemeChoice.Request.Dark && a2.SequenceEqual(["scene.empirescene", "proj"]) &&
          ThemeChoice.FromArguments(["scene.empirescene", "--LightMode", "proj"], out var a3) == ThemeChoice.Request.Light && a3.SequenceEqual(["scene.empirescene", "proj"]),
          "--lightmode and --darkmode work anywhere in the arguments, in any letter case, and are taken out so the scene and project are found as before");
    Check(ThemeChoice.FromArguments(["--lightmode", "x", "--darkmode"], out _) == ThemeChoice.Request.Dark && ThemeChoice.FromArguments(["--darkmode", "--lightmode"], out _) == ThemeChoice.Request.Light &&
          ThemeChoice.FromArguments(["--light-mode", "--dark"], out var a4) == ThemeChoice.Request.System && a4.Length == 2,
          "when both flags are given the last one wins, and look-alikes are not flags");
    int asked = 0;
    bool? Pretend(bool? answer) { asked++; return answer; }
    Check(ThemeChoice.IsLight(ThemeChoice.Request.Light, () => Pretend(false)) && !ThemeChoice.IsLight(ThemeChoice.Request.Dark, () => Pretend(true)) && asked == 0,
          "a flag is obeyed without asking the system");
    Check(ThemeChoice.IsLight(ThemeChoice.Request.System, () => Pretend(true)) && !ThemeChoice.IsLight(ThemeChoice.Request.System, () => Pretend(false)) &&
          !ThemeChoice.IsLight(ThemeChoice.Request.System, () => Pretend(null)) && asked == 3,
          "without a flag the system's setting is used, and dark if it can't be found out");
    Check(ThemeChoice.FromColorScheme("'prefer-dark'\n") == false && ThemeChoice.FromColorScheme("'prefer-light'") == true && ThemeChoice.FromColorScheme("'default'") == true &&
          ThemeChoice.FromColorScheme("") is null && ThemeChoice.FromColorScheme("error: no such schema") is null && ThemeChoice.FromColorScheme(null) is null,
          "GNOME's color-scheme setting is read: prefer-dark is dark, prefer-light and default are light, anything else is unknown");
    Check(ThemeChoice.FromGtkTheme("Adwaita:dark") == false && ThemeChoice.FromGtkTheme("Breeze-Dark") == false && ThemeChoice.FromGtkTheme("Adwaita") is null && ThemeChoice.FromGtkTheme(null) is null,
          "a GTK_THEME that names a dark theme means dark; otherwise it says nothing");
    bool? real = ThemeChoice.SystemIsLight();   // whatever this computer says: it just must not crash or hang
    Check(real.GetValueOrDefault() || !real.GetValueOrDefault(), "asking the real system works");
    Check(Editor.Terminal.TerminalGlyphs.Palette(3) == 0xE5E510 && Editor.Terminal.TerminalGlyphs.Palette(3, light: true) == 0x949800 && Editor.Terminal.TerminalGlyphs.Palette(200) == Editor.Terminal.TerminalGlyphs.Palette(200, light: true),
          "the terminal's basic colors have a set that can be read on white, and the 256-color cube is the same in both");
}

// A script reaches its object's position, rotation and scale through sceneObject.transform.
{
    var box = new Engine.Core.Scene.SceneObject();
    var probe = new TransformProbe();
    typeof(Engine.Core.Scripting.Behaviour).GetProperty("Object")!.SetValue(probe, box);
    probe.sceneObject.transform.position = new System.Numerics.Vector3(1, 2, 3);
    probe.sceneObject.transform.scale *= 2f;
    probe.sceneObject.transform.rotation = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, 1f);
    Check(ReferenceEquals(probe.sceneObject, box), "sceneObject is the object the script is on");
    Check(box.Position == new System.Numerics.Vector3(1, 2, 3) && box.Scale == new System.Numerics.Vector3(2, 2, 2), "transform.position and scale write the object");
    box.Position = new System.Numerics.Vector3(9, 0, 0);
    Check(probe.sceneObject.transform.position.X == 9f && box.Rotation == probe.sceneObject.transform.rotation, "transform reads the object's current values");
    Check(ReferenceEquals(box.transform, box.transform), "an object has one transform");
}

// The same, from a script a user would write, compiled and run like in a game.
{
    string sdir = Path.Combine(Path.GetTempPath(), "empire-sceneobject-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(sdir);
    File.WriteAllText(Path.Combine(sdir, "Mover.cs"), """
        public class Mover : Behaviour
        {
            public float Seen;
            public override void Start() { sceneObject.transform.scale = new Vector3(3, 3, 3); }
            public override void Update()
            {
                sceneObject.transform.position += new Vector3(1, 0, 0);
                sceneObject.transform.rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
                Seen = this.sceneObject.transform.position.X;
            }
        }
        """);
    string sdll = Path.Combine(sdir, "scripts.dll");
    Check(Engine.Scripting.Build.ScriptBuilder.Compile(sdir, sdll,
              Engine.Scripting.Build.ScriptBuilder.DefaultReferences(typeof(Behaviour).Assembly.Location), out string serr),
          "a script that uses sceneObject.transform compiles" + (serr.Length > 0 ? ": " + serr : ""));
    var sasm = Assembly.LoadFrom(sdll);
    var sscene = new Scene();
    var mover = new SceneObject { Name = "Mover" };
    mover.Components.Add(new ScriptComponent { ScriptName = "Mover" });
    sscene.Add(mover);
    ScriptRuntime.Load(sscene, new[] { sasm });
    SceneUpdate.Update(sscene, 1f / 60f);
    SceneUpdate.Update(sscene, 1f / 60f);
    var inst = mover.Components.OfType<ScriptComponent>().Single().Instance!;
    Check(mover.Position.X == 2f && mover.Scale == new Vector3(3, 3, 3) && MathF.Abs(mover.Rotation.Y - MathF.Sin(0.25f)) < 1e-5f &&
          (float)inst.GetType().GetField("Seen")!.GetValue(inst)! == 2f,
          "a running script moves, turns and scales its object through sceneObject.transform");
    Directory.Delete(sdir, true);
}

// The collision blocks of visual code: a script built from blocks, turned into C#, compiled and run against real physics.
{
    var graph = new VisualCode.Model.VcGraph { Name = "Touch" };
    foreach (var v in new[] { "entered", "touching", "ended" })
        graph.Variables.Add(new VisualCode.Model.VcVariable { Name = v, Type = "number", Default = "0" });
    int nodeCounter = 0;
    void Watch(string reporter, string variable)
    {
        string hat = "h" + nodeCounter, cond = "c" + nodeCounter, branch = "i" + nodeCounter, change = "s" + nodeCounter++;
        graph.Nodes.Add(new() { Id = hat, Type = "when_every_frame", Next = branch });
        graph.Nodes.Add(new() { Id = branch, Type = "if", Body = change, Inputs = { ["condition"] = new() { Node = cond } } });
        graph.Nodes.Add(new() { Id = cond, Type = reporter, Inputs = { ["object"] = new() { Literal = "Ground" } } });
        graph.Nodes.Add(new() { Id = change, Type = "change_var", Inputs = { ["var"] = new() { Literal = variable }, ["value"] = new() { Literal = "1" } } });
    }
    Watch("collision_enter", "entered");
    Watch("collision_now", "touching");
    Watch("collision_end", "ended");

    var transpiled = new VisualCode.Transpiler.VcTranspiler().Transpile(graph, "Touch");
    Check(!transpiled.HasErrors && transpiled.Code.Contains("CollisionEnter(\"Ground\")") && transpiled.Code.Contains("Colliding(\"Ground\")") &&
          transpiled.Code.Contains("CollisionExit(\"Ground\")"), "the collision blocks turn into the Behaviour's collision checks");

    string touchDir = Path.Combine(Path.GetTempPath(), "empire-vc-touch-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(touchDir);
    File.WriteAllText(Path.Combine(touchDir, "Touch.cs"), transpiled.Code);
    string touchDll = Path.Combine(touchDir, "touch.dll");
    Check(Engine.Scripting.Build.ScriptBuilder.Compile(touchDir, touchDll,
              Engine.Scripting.Build.ScriptBuilder.DefaultReferences(typeof(Behaviour).Assembly.Location), out string touchErrors),
          "a script using the collision blocks compiles" + (touchErrors.Length > 0 ? ": " + touchErrors : ""));
    var touchAssembly = Assembly.LoadFrom(touchDll);

    var touchScene = PhysicsScene();
    var ball = Dynamic(touchScene, new(0, 1.2f, 0), new SphereCollider());
    ball.Components.Add(new ScriptComponent { ScriptName = "Touch" });
    ScriptRuntime.Load(touchScene, new[] { touchAssembly });
    var touch = ball.Components.OfType<ScriptComponent>().Single().Instance!;
    int Count(string name) => (int)(float)touch.GetType().GetField("_v_" + name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(touch)!;

    void Frames(int n)
    {
        for (int i = 0; i < n; i++)
        {
            touchScene.Physics.Step(touchScene, 1f / 60f);
            SceneUpdate.Update(touchScene, 1f / 60f);
        }
    }
    Frames(2);
    Check(Count("entered") == 0 && Count("touching") == 0, "before the ball lands, no collision block is true");
    Frames(60);
    Check(Count("entered") == 1, "collision enter is true for one frame when the ball lands (" + Count("entered") + ")");
    Check(Count("touching") > 10 && Count("ended") == 0, "collision now stays true while it rests on the ground (" + Count("touching") + ")");
    int whileResting = Count("touching");

    ball.Position = new Vector3(0, 30f, 0);
    ball.Components.OfType<Rigidbody>().Single().Velocity = Vector3.Zero;
    Frames(3);
    Check(Count("ended") == 1, "collision end is true for one frame when it leaves (" + Count("ended") + ")");
    int afterLeaving = Count("touching");
    Frames(10);
    Check(Count("touching") == afterLeaving && afterLeaving <= whileResting + 3 && Count("entered") == 1 && Count("ended") == 1,
          "nothing counts once it is away");
    Directory.Delete(touchDir, true);
}

// Functions in visual code: a "define function" stack that "call function" runs, waits and all.
{
    VisualCode.Model.VcNode Node(string id, string type, string? next = null, string? body = null, params (string Name, string Literal)[] inputs)
    {
        var node = new VisualCode.Model.VcNode { Id = id, Type = type, Next = next, Body = body };
        foreach (var (name, literal) in inputs) node.Inputs[name] = new() { Literal = literal };
        return node;
    }

    var graph = new VisualCode.Model.VcGraph { Name = "Funcs" };
    graph.Variables.Add(new VisualCode.Model.VcVariable { Name = "total", Type = "number", Default = "0" });
    graph.Nodes.Add(Node("start", "when_start", next: "call1"));
    graph.Nodes.Add(Node("call1", "call_function", next: "call2", inputs: ("name", "add five")));
    graph.Nodes.Add(Node("call2", "call_function", next: "after", inputs: ("name", "add five")));
    graph.Nodes.Add(Node("after", "change_var", inputs: new[] { ("var", "total"), ("value", "100") }));
    graph.Nodes.Add(Node("def", "define_function", next: "plus", inputs: ("name", "add five")));
    graph.Nodes.Add(Node("plus", "change_var", next: "pause", inputs: new[] { ("var", "total"), ("value", "5") }));
    graph.Nodes.Add(Node("pause", "wait_frame"));

    var result = new VisualCode.Transpiler.VcTranspiler().Transpile(graph, "Funcs");
    Check(!result.HasErrors && result.Code.Contains("Fn_add_five()") && !result.Code.Contains("Script1"),
          "a function becomes a method, and defining it does not start a script of its own");

    string funcDir = Path.Combine(Path.GetTempPath(), "empire-vc-funcs-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(funcDir);
    File.WriteAllText(Path.Combine(funcDir, "Funcs.cs"), result.Code);
    string funcDll = Path.Combine(funcDir, "funcs.dll");
    Check(Engine.Scripting.Build.ScriptBuilder.Compile(funcDir, funcDll,
              Engine.Scripting.Build.ScriptBuilder.DefaultReferences(typeof(Behaviour).Assembly.Location), out string funcErrors),
          "a script that defines and calls a function compiles" + (funcErrors.Length > 0 ? ": " + funcErrors : ""));
    var funcScene = new Scene();
    var runner = new SceneObject { Name = "Runner" };
    runner.Components.Add(new ScriptComponent { ScriptName = "Funcs" });
    funcScene.Add(runner);
    ScriptRuntime.Load(funcScene, new[] { Assembly.LoadFrom(funcDll) });
    var funcInstance = runner.Components.OfType<ScriptComponent>().Single().Instance!;
    float Total() => (float)funcInstance.GetType().GetField("_v_total", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(funcInstance)!;
    SceneUpdate.Update(funcScene, 1f / 60f);
    Check(Total() == 5f, "a call runs the function until it waits, and the script waits with it (" + Total() + ")");
    SceneUpdate.Update(funcScene, 1f / 60f);
    Check(Total() == 10f, "after the wait the function finishes and the next call starts (" + Total() + ")");
    SceneUpdate.Update(funcScene, 1f / 60f);
    Check(Total() == 110f, "then the blocks after the calls run (" + Total() + ")");
    Directory.Delete(funcDir, true);

    // Mistakes are reported, not compiled
    var missing = new VisualCode.Model.VcGraph();
    missing.Nodes.Add(Node("a", "when_start", next: "b"));
    missing.Nodes.Add(Node("b", "call_function", inputs: ("name", "nowhere")));
    Check(new VisualCode.Transpiler.VcTranspiler().Transpile(missing, "X").Diagnostics.Any(d => d.Message.Contains("no function named 'nowhere'")),
          "calling a function that was never defined is an error");

    var twice = new VisualCode.Model.VcGraph();
    twice.Nodes.Add(Node("a", "define_function", inputs: ("name", "same")));
    twice.Nodes.Add(Node("b", "define_function", inputs: ("name", "same")));
    Check(new VisualCode.Transpiler.VcTranspiler().Transpile(twice, "X").Diagnostics.Any(d => d.Message.Contains("two functions named 'same'")),
          "two functions with one name are an error");

    var loop = new VisualCode.Model.VcGraph();
    loop.Nodes.Add(Node("a", "define_function", next: "ac", inputs: ("name", "ping")));
    loop.Nodes.Add(Node("ac", "call_function", inputs: ("name", "pong")));
    loop.Nodes.Add(Node("b", "define_function", next: "bc", inputs: ("name", "pong")));
    loop.Nodes.Add(Node("bc", "call_function", inputs: ("name", "ping")));
    Check(new VisualCode.Transpiler.VcTranspiler().Transpile(loop, "X").Diagnostics.Count(d => d.Message.Contains("calls itself")) == 2,
          "functions that call each other round and round are an error");
}

// Destroy: any script can destroy any object it has a reference to, at the end of the frame or after a delay.
{
    string destroyDir = Path.Combine(Path.GetTempPath(), "empire-destroy-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(destroyDir);
    File.WriteAllText(Path.Combine(destroyDir, "Reaper.cs"), """
        public class Reaper : Behaviour
        {
            public SceneObject Victim;
            public float Delay;
            public bool SelfDestruct;
            public bool StillThereAfterAsking;
            public int Frame;
            public override void Update()
            {
                Frame++;
                if (Frame == 2)
                {
                    if (SelfDestruct) Destroy(sceneObject);
                    else Destroy(Victim, Delay);
                    StillThereAfterAsking = Scene.FindObjectWithName(Victim?.Name ?? sceneObject.Name) != null;
                    if (!SelfDestruct) Destroy(Victim, Delay);   // asking twice is fine
                }
            }
        }
        public class Bystander : Behaviour
        {
            public int Updates;
            public override void Update() { Updates++; }
        }
        """);
    string destroyDll = Path.Combine(destroyDir, "destroy.dll");
    Check(Engine.Scripting.Build.ScriptBuilder.Compile(destroyDir, destroyDll,
              Engine.Scripting.Build.ScriptBuilder.DefaultReferences(typeof(Behaviour).Assembly.Location), out string destroyErrors),
          "a script that calls Destroy on another object compiles" + (destroyErrors.Length > 0 ? ": " + destroyErrors : ""));
    var destroyAssembly = Assembly.LoadFrom(destroyDll);

    Scene Arena(out SceneObject reaper, out SceneObject victim, out SceneObject child, out SceneObject bystander, bool self = false, float delay = 0f)
    {
        var scene = new Scene();
        victim = new SceneObject { Name = "Victim" };
        child = new SceneObject { Name = "VictimChild" };
        reaper = new SceneObject { Name = "Reaper" };
        bystander = new SceneObject { Name = "Bystander" };
        scene.Add(victim);
        scene.Add(child, victim);
        scene.Add(reaper);
        scene.Add(bystander);
        reaper.Components.Add(new ScriptComponent
        {
            ScriptName = "Reaper",
            Fields = { ["Delay"] = delay.ToString(System.Globalization.CultureInfo.InvariantCulture), ["SelfDestruct"] = self ? "true" : "false" },
        });
        bystander.Components.Add(new ScriptComponent { ScriptName = "Bystander" });
        ScriptRuntime.Load(scene, new[] { destroyAssembly });
        return scene;
    }
    object? Field(SceneObject owner, string script, string field)
    {
        var instance = owner.Components.OfType<ScriptComponent>().First(c => c.ScriptName == script).Instance!;
        return instance.GetType().GetField(field)!.GetValue(instance);
    }

    var arena = Arena(out var reaper, out var victim, out var victimChild, out var bystander);
    reaper.Components.OfType<ScriptComponent>().First().Instance!.GetType().GetField("Victim")!
        .SetValue(reaper.Components.OfType<ScriptComponent>().First().Instance, victim);
    SceneUpdate.Update(arena, 1f / 60f);
    Check(arena.Contains(victim), "nothing is destroyed before the script asks");
    SceneUpdate.Update(arena, 1f / 60f);
    Check((bool)Field(reaper, "Reaper", "StillThereAfterAsking")! && !arena.Contains(victim) && !arena.Contains(victimChild),
          "Destroy waits for the end of the frame, then the object and its children are gone");
    Check(arena.Contains(reaper) && arena.Contains(bystander) && (int)Field(bystander, "Bystander", "Updates")! == 2,
          "the other objects, and the scripts running in the same frame, are not disturbed");
    SceneUpdate.Update(arena, 1f / 60f);
    Check((int)Field(bystander, "Bystander", "Updates")! == 3, "and they carry on");

    var delayed = Arena(out var reaper2, out var victim2, out _, out _, delay: 0.5f);
    reaper2.Components.OfType<ScriptComponent>().First().Instance!.GetType().GetField("Victim")!
        .SetValue(reaper2.Components.OfType<ScriptComponent>().First().Instance, victim2);
    for (int i = 0; i < 20; i++) SceneUpdate.Update(delayed, 1f / 60f);
    Check(delayed.Contains(victim2), "a delayed Destroy leaves the object there until the time is up");
    for (int i = 0; i < 20; i++) SceneUpdate.Update(delayed, 1f / 60f);
    Check(!delayed.Contains(victim2), "and takes it out after");

    var suicide = Arena(out var reaper3, out var victim3, out _, out _, self: true);
    reaper3.Components.OfType<ScriptComponent>().First().Instance!.GetType().GetField("Victim")!
        .SetValue(reaper3.Components.OfType<ScriptComponent>().First().Instance, victim3);
    SceneUpdate.Update(suicide, 1f / 60f);
    SceneUpdate.Update(suicide, 1f / 60f);
    Check(!suicide.Contains(reaper3) && suicide.Contains(victim3), "a script can destroy its own object");
    Scene.Destroy(null);
    Scene.Destroy(reaper3);   // already gone
    SceneUpdate.Update(suicide, 1f / 60f);
    Check(suicide.Contains(victim3), "destroying null, or an object that is already gone, does nothing");
    Directory.Delete(destroyDir, true);
}

// OnDestroy, and the visual code blocks "destroy [object]" and "destroy this object".
{
    string hookDir = Path.Combine(Path.GetTempPath(), "empire-ondestroy-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(hookDir);
    var graph = new VisualCode.Model.VcGraph { Name = "Cleaner" };
    graph.Nodes.Add(new() { Id = "start", Type = "when_start", Next = "kill" });
    graph.Nodes.Add(new() { Id = "kill", Type = "destroy_object", Next = "self", Inputs = { ["object"] = new() { Literal = "Crate" } } });
    graph.Nodes.Add(new() { Id = "self", Type = "destroy_self" });
    var transpiled = new VisualCode.Transpiler.VcTranspiler().Transpile(graph, "Cleaner");
    Check(!transpiled.HasErrors && transpiled.Code.Contains("DestroyObjectsNamed(\"Crate\")") && transpiled.Code.Contains("Destroy(sceneObject)"),
          "the destroy blocks turn into Destroy calls");
    var empty = new VisualCode.Model.VcGraph();
    empty.Nodes.Add(new() { Id = "a", Type = "when_start", Next = "b" });
    empty.Nodes.Add(new() { Id = "b", Type = "destroy_object" });
    Check(new VisualCode.Transpiler.VcTranspiler().Transpile(empty, "X").Diagnostics.Any(d => d.Message.Contains("Choose an object")),
          "a destroy block with no object chosen is reported");
    File.WriteAllText(Path.Combine(hookDir, "Cleaner.cs"), transpiled.Code);
    File.WriteAllText(Path.Combine(hookDir, "Mortal.cs"), """
        using System.Collections.Generic;
        public class Mortal : Behaviour
        {
            public static List<string> Log = new();
            public override void OnDestroy() { Log.Add(sceneObject.Name); }
        }
        public class Grumpy : Behaviour
        {
            public override void OnDestroy() { throw new System.Exception("not today"); }
        }
        """);
    string hookDll = Path.Combine(hookDir, "hooks.dll");
    Check(Engine.Scripting.Build.ScriptBuilder.Compile(hookDir, hookDll,
              Engine.Scripting.Build.ScriptBuilder.DefaultReferences(typeof(Behaviour).Assembly.Location), out string hookErrors),
          "scripts with OnDestroy and the visual code destroy blocks compile" + (hookErrors.Length > 0 ? ": " + hookErrors : ""));
    var hookAssembly = Assembly.LoadFrom(hookDll);
    var log = (List<string>)hookAssembly.GetType("Mortal")!.GetField("Log")!.GetValue(null)!;

    var hookWorld = new Scene();
    var elder = new SceneObject { Name = "Parent" };
    var youngster = new SceneObject { Name = "Kid" };
    var woodCrate = new SceneObject { Name = "Crate" };
    var woodCrate2 = new SceneObject { Name = "Crate" };
    var tidier = new SceneObject { Name = "Cleaner" };
    hookWorld.Add(elder); hookWorld.Add(youngster, elder); hookWorld.Add(woodCrate); hookWorld.Add(woodCrate2); hookWorld.Add(tidier);
    foreach (var o in new[] { elder, youngster, woodCrate })
        o.Components.Add(new ScriptComponent { ScriptName = "Mortal" });
    elder.Components.Add(new ScriptComponent { ScriptName = "Grumpy" });
    tidier.Components.Add(new ScriptComponent { ScriptName = "Cleaner" });
    ScriptRuntime.Load(hookWorld, new[] { hookAssembly });
    SceneUpdate.Update(hookWorld, 1f / 60f);   // Start runs, the cleaner asks, and at the end of the frame it is done
    Check(!hookWorld.Contains(woodCrate) && !hookWorld.Contains(woodCrate2) && !hookWorld.Contains(tidier) && hookWorld.Contains(elder),
          "destroy [object] destroys every object with that name, and destroy this object destroys its own");
    Check(log.SequenceEqual(new[] { "Crate" }), "OnDestroy ran once, on the crate that had started (" + string.Join(",", log) + ")");

    Scene.Destroy(elder);
    SceneUpdate.Update(hookWorld, 1f / 60f);
    Check(!hookWorld.Contains(elder) && !hookWorld.Contains(youngster) && log.SequenceEqual(new[] { "Crate", "Parent", "Kid" }),
          "a parent's OnDestroy runs before its children's, even though its other script threw (" + string.Join(",", log) + ")");

    Directory.Delete(hookDir, true);
}

// The visual code "when destroyed" block: runs when its object is destroyed, up to its first wait.
{
    string whenDir = Path.Combine(Path.GetTempPath(), "empire-whendestroyed-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(whenDir);
    var graph = new VisualCode.Model.VcGraph { Name = "Bomb" };
    graph.Nodes.Add(new() { Id = "hat", Type = "when_destroyed", Next = "boom" });
    graph.Nodes.Add(new() { Id = "boom", Type = "destroy_object", Next = "pause", Inputs = { ["object"] = new() { Literal = "Wall" } } });
    graph.Nodes.Add(new() { Id = "pause", Type = "wait", Next = "late", Inputs = { ["seconds"] = new() { Literal = "1" } } });
    graph.Nodes.Add(new() { Id = "late", Type = "destroy_object", Inputs = { ["object"] = new() { Literal = "Tower" } } });
    var bomb = new VisualCode.Transpiler.VcTranspiler().Transpile(graph, "Bomb");
    Check(!bomb.HasErrors && bomb.Code.Contains("public override void OnDestroy()") && !bomb.Code.Contains("Spawn("),
          "a 'when destroyed' script runs from OnDestroy, not when the scene starts");
    File.WriteAllText(Path.Combine(whenDir, "Bomb.cs"), bomb.Code);
    string whenDll = Path.Combine(whenDir, "bomb.dll");
    Check(Engine.Scripting.Build.ScriptBuilder.Compile(whenDir, whenDll,
              Engine.Scripting.Build.ScriptBuilder.DefaultReferences(typeof(Behaviour).Assembly.Location), out string whenErrors),
          "a 'when destroyed' script compiles" + (whenErrors.Length > 0 ? ": " + whenErrors : ""));
    var whenWorld = new Scene();
    var bombObj = new SceneObject { Name = "Bomb" };
    var wall = new SceneObject { Name = "Wall" };
    var tower = new SceneObject { Name = "Tower" };
    whenWorld.Add(bombObj); whenWorld.Add(wall); whenWorld.Add(tower);
    bombObj.Components.Add(new ScriptComponent { ScriptName = "Bomb" });
    ScriptRuntime.Load(whenWorld, new[] { Assembly.LoadFrom(whenDll) });
    SceneUpdate.Update(whenWorld, 1f / 60f);
    Check(whenWorld.Contains(wall), "nothing happens while the object is still there");
    Scene.Destroy(bombObj);
    SceneUpdate.Update(whenWorld, 1f / 60f);   // the bomb goes; its script asks for the wall, which goes at the next frame's end
    SceneUpdate.Update(whenWorld, 1f / 60f);
    for (int i = 0; i < 120; i++) SceneUpdate.Update(whenWorld, 1f / 60f);
    Check(!whenWorld.Contains(bombObj) && !whenWorld.Contains(wall) && whenWorld.Contains(tower),
          "'when destroyed' runs its steps up to the first wait, and nothing after it");
    Directory.Delete(whenDir, true);
}

// ---- snapping and the FPS counter ----
{
    Check(Math.Abs(Editor.Snapping.Move(0.2f, 0.4f, 0.5f) - 0.3f) < 1e-5f,
          "a snapped move lands the object on the grid (0.2 dragged 0.4 goes to 0.5)");
    Check(Math.Abs(Editor.Snapping.Move(1f, -0.2f, 0.5f)) < 1e-6f, "a small drag stays on the same grid line");
    Check(Editor.Snapping.Move(0.2f, 0.4f, 0f) == 0.4f, "a zero step leaves the move alone");
    Check(Math.Abs(Editor.Snapping.Scale(1.23f, 0.1f) - 1.2f) < 1e-5f && Math.Abs(Editor.Snapping.Scale(0.01f, 0.1f) - 0.1f) < 1e-6f,
          "a snapped scale rounds to the step and never goes below one step");
    Check(Math.Abs(Editor.Snapping.Factor(1.37f, 0.25f) - 1.25f) < 1e-6f, "an even scale changes in whole steps");

    var counter = new Editor.FrameRateCounter();
    for (int i = 0; i <= 60; i++) counter.Tick(i / 60.0);
    Check(Math.Abs(counter.Fps - 60) < 0.5 && Math.Abs(counter.FrameMilliseconds - 1000.0 / 60) < 0.1, "the FPS counter reports 60 frames a second");

    string snapDir = Path.Combine(Path.GetTempPath(), "snap-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(snapDir);
    new ProjectSettings { MoveSnap = 0.25f, ScaleSnap = 0.05f, ShowFpsCounter = true }.Save(snapDir);
    var snapBack = ProjectSettings.Load(snapDir);
    Check(snapBack.MoveSnap == 0.25f && snapBack.ScaleSnap == 0.05f && snapBack.ShowFpsCounter, "snap steps and the FPS counter are saved in project-settings.json");
    Check(new ProjectSettings().MoveSnap == 0.5f && new ProjectSettings { MoveSnap = -3f }.MoveSnap == ProjectSettings.MinSnap &&
          new ProjectSettings { ScaleSnap = float.NaN }.ScaleSnap == 0.1f, "snap steps default to 0.5 and 0.1 and stay in range");
    Directory.Delete(snapDir, true);
}

// ---- tags, triggers, prefs, fog and camera effects ----
{
    // Tags
    var tagScene = new Scene();
    var coinA = new SceneObject { Name = "Coin", Tag = "Coin" };
    var coinB = new SceneObject { Name = "Gold", Tag = "Coin" };
    var plain = new SceneObject { Name = "Rock" };
    tagScene.Add(coinA); tagScene.Add(plain); tagScene.Add(coinB);
    Check(tagScene.FindByTag("Coin") == coinA && tagScene.FindAllByTag("Coin").Count == 2 && tagScene.FindByTag("coin") is null,
          "objects are found by tag, exactly");
    Check(coinA.CompareTag("Coin") && !plain.CompareTag("") && !plain.CompareTag("Coin"), "CompareTag matches only a set tag");
    var tagBack = SceneSerializer.FromJson(SceneSerializer.ToJson(tagScene));
    Check(tagBack.Find(coinB.Id)!.Tag == "Coin" && tagBack.Find(plain.Id)!.Tag == "" && !SceneSerializer.ToJson(tagScene).Contains("\"tag\": \"\""),
          "tags survive saving and loading, and an empty tag is left out");

    // Triggers: a falling ball passes through a trigger zone, which reports it, and still lands on the ground.
    var zoneScene = PhysicsScene();
    var zone = new SceneObject { Name = "Zone", Position = new(0, 2, 0) };
    zone.Components.Add(new BoxCollider { Size = new(4, 1, 4), IsTrigger = true });
    zoneScene.Add(zone);
    var ball = Dynamic(zoneScene, new(0, 4, 0), new SphereCollider());
    ball.Name = "Ball";
    ball.Components.Add(new ScriptComponent { ScriptName = nameof(TriggerProbe) });
    zone.Components.Add(new ScriptComponent { ScriptName = nameof(TriggerProbe) });
    TriggerProbe.Log.Clear();
    ScriptRuntime.Load(zoneScene, typeof(TriggerProbe).Assembly);
    Simulate(zoneScene, 3);
    Check(MathF.Abs(ball.Position.Y - 0.5f) < 0.03f, "a trigger doesn't stop a falling ball; the ground does");
    Check(TriggerProbe.Log.Contains("Ball enter Zone") && TriggerProbe.Log.Contains("Ball exit Zone") &&
          TriggerProbe.Log.Contains("Zone enter Ball") && TriggerProbe.Log.Contains("Zone exit Ball") &&
          !TriggerProbe.Log.Any(l => l.Contains("Ground")),
          "both objects hear OnTriggerEnter and OnTriggerExit, and touching the ground is not a trigger (" + string.Join("; ", TriggerProbe.Log) + ")");
    Check(Engine.Core.Scripting.Physics.Raycast(new Vector3(0, 10, 0), -Vector3.UnitY, out var zoneHit, 20f, ignore: ball) && zoneHit.Object.Name == "Ground",
          "raycasts pass through triggers");
    var trigBack = SceneSerializer.FromJson(SceneSerializer.ToJson(zoneScene)).Find(zone.Id)!.Components.OfType<BoxCollider>().Single();
    Check(trigBack.IsTrigger, "Is Trigger survives saving and loading");

    // Visual code: the trigger and tag blocks
    var vcGraph = new VisualCode.Model.VcGraph { Name = "Zones" };
    vcGraph.Nodes.Add(new() { Id = "h", Type = "when_every_frame", Next = "if1" });
    vcGraph.Nodes.Add(new() { Id = "if1", Type = "if", Inputs = { ["condition"] = new() { Node = "t1" } }, Next = "if2" });
    vcGraph.Nodes.Add(new() { Id = "t1", Type = "trigger_enter", Inputs = { ["object"] = new() { Literal = "Zone" } } });
    vcGraph.Nodes.Add(new() { Id = "if2", Type = "if", Inputs = { ["condition"] = new() { Node = "t2" } } });
    vcGraph.Nodes.Add(new() { Id = "t2", Type = "collision_now_tag", Inputs = { ["tag"] = new() { Literal = "Enemy" } } });
    var vcOut = new VisualCode.Transpiler.VcTranspiler().Transpile(vcGraph, "Zones");
    Check(!vcOut.HasErrors && vcOut.Code.Contains("TriggerEnter(\"Zone\")") && vcOut.Code.Contains("CollidingTag(\"Enemy\")"),
          "the trigger and tag blocks turn into code" + (vcOut.HasErrors ? ": " + string.Join("; ", vcOut.Diagnostics.Select(d => d.Message)) : ""));
    string vcDir = Path.Combine(Path.GetTempPath(), "empire-zones-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(vcDir);
    File.WriteAllText(Path.Combine(vcDir, "Zones.cs"), vcOut.Code);
    Check(Engine.Scripting.Build.ScriptBuilder.Compile(vcDir, Path.Combine(vcDir, "zones.dll"),
              Engine.Scripting.Build.ScriptBuilder.DefaultReferences(typeof(Behaviour).Assembly.Location), out string zoneErrors),
          "a script with the trigger and tag blocks compiles" + (zoneErrors.Length > 0 ? ": " + zoneErrors : ""));
    Directory.Delete(vcDir, true);

    // Prefs
    string prefsDir = Path.Combine(Path.GetTempPath(), "empire-prefs-" + Guid.NewGuid().ToString("N"));
    string prefsFile = Path.Combine(prefsDir, "prefs.json");
    Prefs.FilePath = prefsFile;
    Check(Prefs.GetInt("highScore") == 0 && Prefs.GetString("name", "Player") == "Player" && !Prefs.HasKey("highScore"),
          "unset prefs give their defaults");
    Prefs.SetInt("highScore", 120);
    Prefs.SetFloat("volume", 0.8f);
    Prefs.SetBool("music", true);
    Prefs.SetString("name", "Ada");
    Prefs.FilePath = Path.Combine(prefsDir, "other.json");   // forget what is in memory...
    Prefs.FilePath = prefsFile;                                // ...and read the file again
    Check(Prefs.GetInt("highScore") == 120 && Prefs.GetFloat("volume") == 0.8f && Prefs.GetBool("music") && Prefs.GetString("name") == "Ada",
          "prefs are written at once and read back from the file");
    Prefs.DeleteKey("name");
    Check(!Prefs.HasKey("name") && Prefs.HasKey("highScore"), "a pref can be deleted");
    Prefs.DeleteAll();
    Check(!Prefs.HasKey("highScore"), "every pref can be deleted");
    Directory.Delete(prefsDir, true);

    // Fog and camera effects
    var envScene = new Scene();
    var envObj = new SceneObject { Name = "Environment" };
    envObj.Components.Add(new SceneEnvironment { Fog = true, FogColor = new(0.5f, 0.6f, 0.7f), FogStart = 10f, FogEnd = 50f });
    var camObj = new SceneObject { Name = "Camera" };
    camObj.Components.Add(new CameraComponent { Exposure = 0.5f, Contrast = 1.2f, Vignette = 0.3f, Sharpen = 0.4f });
    envScene.Add(envObj); envScene.Add(camObj);
    var envBack = SceneSerializer.FromJson(SceneSerializer.ToJson(envScene));
    var env2 = envBack.Find(envObj.Id)!.Components.OfType<SceneEnvironment>().Single();
    var cam2 = envBack.Find(camObj.Id)!.Components.OfType<CameraComponent>().Single();
    Check(env2.Fog && env2.FogColor == new Vector3(0.5f, 0.6f, 0.7f) && env2.FogStart == 10f && env2.FogEnd == 50f, "fog settings survive saving and loading");
    Check(cam2.Exposure == 0.5f && cam2.Contrast == 1.2f && cam2.Vignette == 0.3f && cam2.Sharpen == 0.4f, "camera effects survive saving and loading");
    var fogData = Engine.Core.Rendering.EnvironmentData.From(env2);
    Check(fogData.Fog && fogData.FogEnd == 50f && !Engine.Core.Rendering.EnvironmentData.From(new SceneEnvironment { Fog = true, FogStart = 50f, FogEnd = 10f }).Fog,
          "fog reaches the renderer, and is off when its end is nearer than its start");
    var plainCam = new SceneObject();
    plainCam.Components.Add(new CameraComponent());
    var plainScene = new Scene(); plainScene.Add(plainCam);
    string plainJson = SceneSerializer.ToJson(plainScene);
    Check(!plainJson.Contains("exposure") && !plainJson.Contains("vignette"), "a camera without effects saves none");
}

// ---- double positions ----
{
    var before = SceneObject.PositionPrecision;
    try
    {
        SceneObject.PositionPrecision = PositionType.Float;
        var fo = new SceneObject { PositionD = new Vector3D(1e9 + 0.123, 0.1, 2) };
        Check(fo.PositionD == fo.PositionD.ToFloatPrecision() && fo.Position.Y == 0.1f, "float positions round to a float, as before");
        var fScene = new Engine.Core.Scene.Scene(); fScene.Add(fo);
        Check(!SceneSerializer.ToJson(fScene).Contains("positionD"), "a float project writes no positionD");

        SceneObject.PositionPrecision = PositionType.Double;
        var parentObj = new SceneObject { PositionD = new Vector3D(1e12 + 0.5, 0, 0) };
        var childObj = new SceneObject { PositionD = new Vector3D(0, 0, 0.25) };
        var dScene = new Engine.Core.Scene.Scene(); dScene.Add(parentObj); dScene.Add(childObj, parentObj);
        Check(parentObj.PositionD.X == 1e12 + 0.5, "double positions keep digits a float loses");
        var walker = new SceneObject { PositionD = new Vector3D(1e9, 0, 0) };
        for (int i = 0; i < 1000; i++) walker.PositionD += new Vector3D(0.001, 0, 0);
        Check(Math.Abs(walker.PositionD.X - (1e9 + 1.0)) < 1e-3, "small steps far from the origin add up in doubles");
        parentObj.PositionD += new Vector3D(1, 0, 0);
        Check(Math.Abs(childObj.WorldPositionD.X - parentObj.PositionD.X) < 1e-9 && Math.Abs(childObj.WorldPositionD.Z - 0.25) < 1e-9, "world position is worked out in doubles through the parent");
        parentObj.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        Check(Math.Abs(childObj.WorldPositionD.X - (parentObj.PositionD.X + 0.25)) < 1e-4, "a rotated parent turns the child's offset");
        string json = SceneSerializer.ToJson(dScene);
        var back = SceneSerializer.FromJson(json).Find(parentObj.Id)!;
        Check(json.Contains("positionD") && back.PositionD == parentObj.PositionD, "double positions survive saving and loading");
        Check(Math.Abs(back.Position.X - (float)parentObj.PositionD.X) == 0f, "the float position is still written for older readers");
        var bodyTf = parentObj.transform; bodyTf.positionD = new Vector3D(3, 4, 5);
        Check(parentObj.Position == new Vector3(3, 4, 5) && bodyTf.positionD.Y == 4, "transform.positionD reads and writes the object");

        string psTemp = Path.Combine(Path.GetTempPath(), "ps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(psTemp);
        new ProjectSettings { PositionType = PositionType.Double }.Save(psTemp);
        Check(ProjectSettings.Load(psTemp).PositionType == PositionType.Double && ProjectSettings.Load(Path.GetTempPath()).PositionType == PositionType.Float, "the position type is saved in project-settings.json (float by default)");
        Directory.Delete(psTemp, true);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<GameManifest>("{\"name\":\"g\",\"startScene\":\"s\",\"positionType\":\"Double\"}", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Check(manifest!.PositionType == PositionType.Double && System.Text.Json.JsonSerializer.Deserialize<GameManifest>("{\"name\":\"g\",\"startScene\":\"s\"}")!.PositionType == PositionType.Float, "the game manifest carries the position type");
    }
    finally { SceneObject.PositionPrecision = before; }
}

// ---- anti-aliasing modes ----
{
    foreach (var mode in new[] { AntiAliasingMode.None, AntiAliasingMode.Msaa, AntiAliasingMode.Fxaa, AntiAliasingMode.Smaa })
    {
        var aaScene = new Engine.Core.Scene.Scene();
        var camObj = new SceneObject { Name = "Cam" };
        camObj.Components.Add(new CameraComponent { AntiAliasing = mode, MsaaSamples = 8 });
        aaScene.Add(camObj);
        string aaJson = SceneSerializer.ToJson(aaScene);
        var aaBack = SceneSerializer.FromJson(aaJson).All.Single().Components.OfType<CameraComponent>().Single();
        Check(aaBack.AntiAliasing == mode, $"the camera's {mode} anti-aliasing survives saving and loading");
        Check(aaJson.Contains("msaaSamples") == (mode == AntiAliasingMode.Msaa), $"{mode}: the MSAA sample count is saved only for MSAA");
        Check(new CameraComponent { AntiAliasing = mode, MsaaSamples = 8 }.RequestedSamples == (mode == AntiAliasingMode.Msaa ? 8 : 1), $"{mode}: only MSAA asks for extra samples");
    }
}

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

// A stand-in for GitHub: answers every request with whatever the test says.

sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(respond(request));
    }
}

// Reports progress right away on the calling thread (Progress<T> posts to another thread, which a test can't wait on).
sealed class SyncProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
{
    public void Report(InstallProgress value) => report(value);
}

public class CounterScript : Behaviour
{
    public int count;
    public int startCount;
    public int loadedCalls;
    public override void Start() { startCount++; }
    public override void Update() { count++; }
    public override void OnGameLoaded() { loadedCalls++; }
}

public class FieldsScript : Behaviour
{
    public float speed = 2.5f;
    public int lives = 3;
    public string title = "Hi";
    public float[] points = { 1f, 2f };
    public List<string> names = new();
    public Vector3 notBuiltIn;   // shown since Vector3 fields became editable
#pragma warning disable CS0414   // never read on purpose: private fields must stay out of the Inspector
    private int hidden = 1;
#pragma warning restore CS0414
    public readonly int fixedValue = 2;
}

class TransformProbe : Engine.Core.Scripting.Behaviour { }

public class TriggerProbe : Behaviour
{
    public static List<string> Log = new();
    public override void OnTriggerEnter(SceneObject other) => Log.Add($"{sceneObject.Name} enter {other.Name}");
    public override void OnTriggerExit(SceneObject other) => Log.Add($"{sceneObject.Name} exit {other.Name}");
}
