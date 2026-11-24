using System.Numerics;
using Brutal.Numerics;
using Engine.Brutal.Gfx;
using Engine.Brutal.Metal;
using Engine.Brutal.Rendering;
using Engine.Brutal.Ui;
using Engine.Core.Assets;
using Engine.Core.Rendering;
using Engine.Core.Scene;
using Engine.Core.Scripting;

// Draws with the Metal renderer off screen and checks the picture. Run on a Mac with Apple silicon; set MTL_DEBUG_LAYER=1 to have
// Metal check every call too (any misuse then stops the program with Metal's own message).
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

if (!OperatingSystem.IsMacOS())
{
    Console.WriteLine("Metal only exists on a Mac: nothing to check here.");
    return 0;
}

const int Width = 320, Height = 180;
using var ctx = new MetalContext(Width, Height);
Console.WriteLine($"GPU: {ctx.DeviceName}");
using var shaders = new ShaderLibrary(null);

// Every shader the engine has translates to Metal and compiles.
var broken = new List<string>();
foreach (var shader in ShaderPack.All)
{
    try { ctx.Function(shaders, shader); }
    catch (Exception ex) { broken.Add($"{shader.Name}: {ex.Message}"); }
}
foreach (var line in broken) Console.WriteLine("  " + line);
Check(broken.Count == 0, $"all {ShaderPack.All.Count} of the engine's shaders translate to Metal and compile");

// A frame: a red cube in the middle, a yellow one above it, in front of a blue sky, with a green UI square in the top-left corner.
using var renderer = new MetalRenderer(ctx, shaders);
using var canvas = new Canvas2D(ctx, shaders, Canvas2D.DefaultFont(), 14f);
renderer.UploadModel(Primitives.Cube, Primitives.Get(Primitives.Cube));

var eye = new Vector3(0f, 0f, 4f);
var viewProj = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY) * VulkanMath.Projection(MathF.PI / 3f, SceneLayout.Aspect, 0.1f);
var sky = new EnvironmentData(SkyMode.Color, new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 1f), "", 1f, new Vector3(0.6f), 0f);
var upper = new Vector3(0f, 1.15f, 0f);

byte[] Frame(Action<ISceneRenderer>? extra = null, Action<Canvas2D>? ui = null)
{
    renderer.SetLighting(sky, Array.Empty<LightData>());
    renderer.SetShadows(true, 1f, 20f, 1024);
    renderer.BeginFrame(viewProj, eye, Vector3.Normalize(new Vector3(0.3f, 1f, 0.6f)), new Vector3(1f));
    renderer.DrawModel(Primitives.Cube, Matrix4x4.Identity, new Vector4(1f, 0f, 0f, 1f));
    renderer.DrawModel(Primitives.Cube, Matrix4x4.CreateScale(0.4f) * Matrix4x4.CreateTranslation(upper), new Vector4(1f, 1f, 0f, 1f));
    extra?.Invoke(renderer);
    canvas.Begin();
    canvas.Rect(0, 0, 20, 20, new float4(0f, 1f, 0f, 1f));
    ui?.Invoke(canvas);
    renderer.EndFrame(pass => canvas.Render(pass, Width, Height, new float2(1f, 1f)));
    return ctx.ReadPixels();
}

// Where a world point lands on the picture, the way Vulkan maps clip space (y pointing down).
(int X, int Y) OnScreen(Vector3 world)
{
    var vp = ctx.SceneViewport;
    var clip = Vector4.Transform(new Vector4(world, 1f), viewProj);
    float x = (clip.X / clip.W * 0.5f + 0.5f) * vp.Z * Width + vp.X * Width;
    float y = (clip.Y / clip.W * 0.5f + 0.5f) * vp.W * Height + vp.Y * Height;
    return ((int)x, (int)y);
}

(int R, int G, int B) Pixel(byte[] p, (int X, int Y) at)
{
    int i = (at.Y * Width + at.X) * 4;
    return (p[i + 2], p[i + 1], p[i]);   // the picture is blue, green, red, alpha
}

bool Reddish((int R, int G, int B) c) => c.R > 80 && c.R > c.G * 2 && c.R > c.B * 2;
bool Yellowish((int R, int G, int B) c) => c.R > 80 && c.G > 80 && c.B < Math.Min(c.R, c.G) / 2;
bool Bluish((int R, int G, int B) c) => c.B > 150 && c.R < 60 && c.G < 60;
bool Greenish((int R, int G, int B) c) => c.G > 200 && c.R < 60 && c.B < 60;

var pixels = Frame();
Check(!ctx.HadGpuError, "the frame finishes on the GPU");
Check(Reddish(Pixel(pixels, OnScreen(Vector3.Zero))), $"the red cube is in the middle {Pixel(pixels, OnScreen(Vector3.Zero))}");
Check(Yellowish(Pixel(pixels, OnScreen(upper))), $"the yellow cube is above it, where Vulkan puts it {Pixel(pixels, OnScreen(upper))}");
Check(Bluish(Pixel(pixels, (Width - 40, 40))), $"the sky fills the rest {Pixel(pixels, (Width - 40, 40))}");
Check(Greenish(Pixel(pixels, (5, 5))), $"the UI is drawn over the scene, from the top-left corner {Pixel(pixels, (5, 5))}");
Check(Bluish(Pixel(pixels, (5, 40))), "and only where it draws");

// The scene keeps to its 16:9 rectangle: with room kept below it for panels, the rest is the backdrop color.
ctx.BottomReservedFraction = 0.5f;
pixels = Frame();
var below = Pixel(pixels, (Width / 2, Height - 5));
Check(below.B is > 30 and < 45 && below.R < 20, $"outside the scene rectangle is the backdrop {below}");
Check(Reddish(Pixel(pixels, OnScreen(Vector3.Zero))), "and the scene is drawn inside it");
ctx.BottomReservedFraction = 0f;

// See-through models, particles and a shadow-casting sun all draw.
pixels = Frame(r =>
{
    r.DrawModel(Primitives.Cube, Matrix4x4.CreateTranslation(1.6f, 0f, 0f), new Vector4(0f, 1f, 0f, 0.5f));
    var particles = new[] { new ParticleInstance { Position = new Vector3(-1.6f, 0f, 0f), Size = 0.8f, Color = new Vector4(1f, 1f, 1f, 1f) } };
    r.DrawParticles(particles, ParticleBlend.Alpha, ParticleSprite.Square);
});
var seeThrough = Pixel(pixels, OnScreen(new Vector3(1.6f, 0f, 0f)));
Check(seeThrough.G > 80 && seeThrough.B > 40, $"a half see-through green cube shows the sky through it {seeThrough}");
var particle = Pixel(pixels, OnScreen(new Vector3(-1.6f, 0f, 0f)));
Check(particle.R > 200 && particle.G > 200 && particle.B > 200, $"a white particle sprite is drawn {particle}");

// The sun's shadow: a white floor, lit from behind the cubes, is darker in front of them than off to the side.
var floorAt = new Vector3(0f, -0.95f, 1.0f);
var litAt = new Vector3(2.2f, -0.95f, 1.0f);
byte[] Floor(bool shadows)
{
    renderer.SetLighting(sky, Array.Empty<LightData>());
    renderer.SetShadows(shadows, 1f, 20f, 2048);
    renderer.BeginFrame(viewProj, eye, Vector3.Normalize(new Vector3(0f, 1f, -1f)), new Vector3(1f));
    renderer.DrawModel(Primitives.Cube, Matrix4x4.Identity, new Vector4(1f, 0f, 0f, 1f));
    renderer.DrawModel(Primitives.Cube, Matrix4x4.CreateScale(8f, 0.1f, 8f) * Matrix4x4.CreateTranslation(0f, -1f, 0f), Vector4.One);
    renderer.EndFrame(null);
    return ctx.ReadPixels();
}
pixels = Floor(shadows: true);
var shaded = Pixel(pixels, OnScreen(floorAt));
var lit = Pixel(pixels, OnScreen(litAt));
Check(shaded.R + 40 < lit.R, $"the cube's shadow darkens the floor {shaded} against {lit}");
pixels = Floor(shadows: false);
Check(Math.Abs(Pixel(pixels, OnScreen(floorAt)).R - Pixel(pixels, OnScreen(litAt)).R) < 25, "and with shadows off it doesn't");

// Reversed depth: the near plane is at depth 1 and depth falls toward 0 with distance. A perspective camera has no far plane, so even
// something a billion units away still has a depth above 0; an orthographic one reaches 0 at its far plane. A ray through the
// middle of the screen goes where the camera looks, for both.
float DepthAt(Matrix4x4 projection, float distance)
{
    var clip = Vector4.Transform(new Vector4(0f, 0f, -distance, 1f), projection);
    return clip.Z / clip.W;
}
var perspective = VulkanMath.Projection(MathF.PI / 3f, 16f / 9f, 0.1f);
var orthographic = VulkanMath.Orthographic(10f, 16f / 9f, 0.1f, 5000f);
float billion = DepthAt(perspective, 1e9f);
Check(MathF.Abs(DepthAt(perspective, 0.1f) - ClipDepth.Near) < 1e-5f && billion > ClipDepth.Far && billion < DepthAt(perspective, 1e6f),
    $"a perspective camera puts its near plane at depth 1 and has no far plane ({DepthAt(perspective, 0.1f)}, {billion} a billion units away)");
Check(MathF.Abs(DepthAt(orthographic, 0.1f) - ClipDepth.Near) < 1e-5f && MathF.Abs(DepthAt(orthographic, 5000f) - ClipDepth.Far) < 1e-5f,
    $"an orthographic camera goes from depth 1 at its near plane to 0 at its far plane ({DepthAt(orthographic, 0.1f)}, {DepthAt(orthographic, 5000f)})");
var farEye = new Vector3(0f, 1000f, 3500f);
var farView = Matrix4x4.CreateLookAt(farEye, Vector3.Zero, Vector3.UnitY);
foreach (var (name, projection) in new[] { ("perspective", perspective), ("orthographic", orthographic) })
{
    Input.SetView(new Vector2(1600f, 900f), farView * projection);
    var ray = Input.ScreenPointToRay(new Vector2(800f, 450f));
    Check(Vector3.Dot(ray.Direction, Vector3.Normalize(-farEye)) > 0.9999f && Vector3.Distance(ray.Origin, farEye) < 1f,
        $"Input.ScreenPointToRay through the middle of the {name} camera starts at it and goes where it looks");
}

// Big things far away don't flicker through each other: a huge white floor some 3600 units off, with a dark slab only 0.5 below
// its top, and the camera's near plane at 0.1. Without reversed depth the slab showed through, in stripes at its far edge.
var groundTop = 0.5f;
var farViewProj = farView * VulkanMath.Projection(MathF.PI / 4f, SceneLayout.Aspect, 0.1f);
renderer.SetLighting(sky, Array.Empty<LightData>());
renderer.SetShadows(true, 1f, 40f, 2048);
renderer.BeginFrame(farViewProj, farEye, Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f)), new Vector3(1f));
// The slab first, as in a scene where the floor is the last object: a depth that rounds the same as the slab's then loses.
renderer.DrawModel(Primitives.Cube, Matrix4x4.CreateScale(3000f, 1f, 3000f) * Matrix4x4.CreateTranslation(0f, groundTop - 1f, 0f),
    new Vector4(0.02f, 0.02f, 0.02f, 1f));
renderer.DrawModel(Primitives.Cube, Matrix4x4.CreateScale(4000f, 1f, 4000f), Vector4.One);
renderer.EndFrame(null);
pixels = ctx.ReadPixels();
int floorPixels = 0, darkPixels = 0;
for (float x = -1400f; x <= 1400f; x += 25f)
    for (float z = -1400f; z <= 600f; z += 25f)
    {
        var clip = Vector4.Transform(new Vector4(x, groundTop, z, 1f), farViewProj);
        if (clip.W <= 0f || MathF.Abs(clip.X / clip.W) > 0.95f || MathF.Abs(clip.Y / clip.W) > 0.95f) continue;
        var vp = ctx.SceneViewport;
        int px = (int)((clip.X / clip.W * 0.5f + 0.5f) * vp.Z * Width + vp.X * Width);
        int py = (int)((clip.Y / clip.W * 0.5f + 0.5f) * vp.W * Height + vp.Y * Height);
        floorPixels++;
        if (Pixel(pixels, (px, py)).R < 60) darkPixels++;
    }
Check(!ctx.HadGpuError && floorPixels > 100 && darkPixels == 0,
    $"a far-off floor doesn't flicker with a surface 0.5 below it ({darkPixels} dark of {floorPixels} floor pixels)");

// A terrain is drawn from the vertices sent with each frame while it changes, and as an ordinary uploaded mesh once it stands still.
// Both must show the same picture, and a change must show at once, also after it has settled.
{
    var scene = new Scene();
    var ground = new SceneObject { Name = "Ground" };
    var terrain = new Terrain { Width = 20f, Length = 20f, Height = 10f, Collide = false };
    terrain.Flatten(0.2f);
    ground.Components.Add(terrain);
    scene.Add(ground);
    var drawer = new TerrainDrawer();
    var eyeAbove = new Vector3(0f, 14f, 0.01f);
    var looking = Matrix4x4.CreateLookAt(eyeAbove, Vector3.Zero, -Vector3.UnitZ) * VulkanMath.Projection(MathF.PI / 3f, SceneLayout.Aspect, 0.1f);
    byte[] TerrainFrame()
    {
        drawer.Sync(renderer, scene);
        renderer.SetLighting(sky, Array.Empty<LightData>());
        renderer.SetShadows(false, 1f, 20f, 512);
        renderer.BeginFrame(looking, eyeAbove, Vector3.Normalize(new Vector3(0.3f, 1f, 0.6f)), new Vector3(1f));
        drawer.Draw(renderer, scene);
        renderer.EndFrame(null);
        return ctx.ReadPixels();
    }
    var spot = OnScreen2(new Vector3(0f, 2f, 0f));
    (int R, int G, int B) Spot(byte[] p) => Pixel(p, spot);
    (int X, int Y) OnScreen2(Vector3 world)
    {
        var vp = ctx.SceneViewport;
        var clip = Vector4.Transform(new Vector4(world, 1f), looking);
        return ((int)((clip.X / clip.W * 0.5f + 0.5f) * vp.Z * Width + vp.X * Width), (int)((clip.Y / clip.W * 0.5f + 0.5f) * vp.W * Height + vp.Y * Height));
    }
    byte[] before = TerrainFrame();
    for (int i = 0; i < 30; i++) TerrainFrame();
    var settled = Spot(TerrainFrame());
    terrain.Flatten(0.9f);   // a much higher plain: nearer the camera, so brighter and a different size on screen
    var changed = TerrainFrame();
    Check(!ctx.HadGpuError && Spot(before) == settled, $"a terrain looks the same once it has settled into an uploaded mesh {Spot(before)} {settled}");
    byte[] afterEdit = changed;
    for (int i = 0; i < 30; i++) afterEdit = TerrainFrame();
    Check(!changed.AsSpan().SequenceEqual(before) && changed.AsSpan().SequenceEqual(afterEdit), "a change shows at once, and looks the same after it has settled");
}

// No maximum viewing distance: a red cube 300,000 units away, big enough to see, is drawn in front of the sky.
var distantAt = new Vector3(0f, 0f, -300_000f);
renderer.SetLighting(sky, Array.Empty<LightData>());
renderer.SetShadows(true, 1f, 40f, 2048);
renderer.BeginFrame(viewProj, eye, Vector3.Normalize(new Vector3(0.3f, 1f, 0.6f)), new Vector3(1f));
renderer.DrawModel(Primitives.Cube, Matrix4x4.CreateScale(40_000f) * Matrix4x4.CreateTranslation(distantAt), new Vector4(1f, 0f, 0f, 1f));
renderer.EndFrame(null);
pixels = ctx.ReadPixels();
Check(!ctx.HadGpuError && Reddish(Pixel(pixels, OnScreen(distantAt))), $"a cube 300,000 units away is drawn {Pixel(pixels, OnScreen(distantAt))}");

// MSAA, FXAA, SMAA and the color effects.
foreach (var (name, camera) in new (string, CameraComponent)[]
{
    ("4x MSAA", new CameraComponent { MsaaSamples = 4, AntiAliasing = AntiAliasingMode.Msaa }),
    ("FXAA", new CameraComponent { AntiAliasing = AntiAliasingMode.Fxaa }),
    ("SMAA", new CameraComponent { AntiAliasing = AntiAliasingMode.Smaa }),
    ("color effects", new CameraComponent { Exposure = 0.5f, Vignette = 0.3f }),
    ("SMAA with color effects", new CameraComponent { AntiAliasing = AntiAliasingMode.Smaa, Contrast = 1.2f }),
})
{
    ctx.ApplyCamera(camera);
    pixels = Frame();
    Check(!ctx.HadGpuError && Reddish(Pixel(pixels, OnScreen(Vector3.Zero))) && Greenish(Pixel(pixels, (5, 5))),
        $"{name}: the frame draws the same picture {Pixel(pixels, OnScreen(Vector3.Zero))}");
}
ctx.ApplyCamera(null);

// The sprite editor, drawn with a fake window and mouse.
SpriteEditorChecks.Run(shaders, Check);

Console.WriteLine(failures == 0 ? "\nAll Metal checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
