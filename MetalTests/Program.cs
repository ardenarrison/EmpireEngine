using System.Numerics;
using Brutal.Numerics;
using Engine.Brutal.Gfx;
using Engine.Brutal.Metal;
using Engine.Brutal.Rendering;
using Engine.Brutal.Ui;
using Engine.Core.Assets;
using Engine.Core.Rendering;
using Engine.Core.Scene;

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
var viewProj = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY) * VulkanMath.Projection(MathF.PI / 3f, SceneLayout.Aspect, 0.1f, 100f);
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

Console.WriteLine(failures == 0 ? "\nAll Metal checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
