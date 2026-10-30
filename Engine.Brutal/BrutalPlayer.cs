using System.Diagnostics;
using System.Numerics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Engine.Brutal.Gfx;
using Engine.Brutal.Rendering;
using Engine.Brutal.Ui;
using Engine.Core.Assets;
using Engine.Core.Rendering;
using Engine.Core.Scene;
using Engine.Core.Scripting;

namespace Engine.Brutal;

// Opens a window and draws every MeshRenderer in a scene each frame, with the game's UI on top.
// Uses the scene's first CameraComponent. Without one, the camera orbits the scene.
public sealed class BrutalPlayer
{
    // Text size of the game's UI, in screen points. The editor's play mode uses the same size.
    public const float UiFontSize = 18f;

    private readonly AssetCache _assets;

    public BrutalPlayer(AssetCache assets) => _assets = assets;

    public void Run(Scene scene, string title = "Empire Engine")
    {
        BundledVulkan.Prepare();
        if (!Glfw.Init())
            throw new InvalidOperationException("GLFW failed to initialize");
        try
        {
            Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
            var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo
            {
                Title = title,
                Size = new int2(1280, 720),
            });
            AppIcon.Apply(window);
            try
            {
                RenderLoop(window, scene);
            }
            finally
            {
                window.Dispose();
            }
        }
        finally
        {
            Glfw.Terminate();
        }
    }

    private void RenderLoop(GlfwWindow window, Scene scene)
    {
        // Declaration order matters: renderer, then shaders, then context (disposed in reverse).
        using var ctx = new VulkanContext(window, 1280, 720);
        using var shaders = new ShaderLibrary(ctx.Device);
        using var renderer = new BrutalRenderer(ctx, shaders);
        using var uiCanvas = new Canvas2D(ctx, shaders, Canvas2D.DefaultFont(), UiFontSize, window.ContentScale.X);

        // Every game opens with the Empire Engine and BRUTAL logos, before anything else loads or runs (the BRUTAL
        // License requires it). It can't be skipped.
        SplashScreen.Run(ctx, shaders, window, uiCanvas);
        if (window.ShouldClose) return;

        var models = scene.All
            .SelectMany(o => o.Components.OfType<MeshRenderer>())
            .Select(c => c.Model)
            .Where(m => !string.IsNullOrEmpty(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var uploaded = new HashSet<string>(models, StringComparer.OrdinalIgnoreCase);   // models from scenes loaded later are uploaded when first drawn
        foreach (var key in models)
            renderer.UploadModel(key, _assets.GetModel(key));
        renderer.AfterSceneDraws = cmd => VulkanScripts.Render(scene, cmd);

        Engine.Core.Physics.PhysicsWorld.ModelLoader = _assets.GetModel;   // for mesh colliders
        Animator.ModelLoader = _assets.GetModel;                           // and for animations
        var terrains = new TerrainDrawer();
        var scriptMeshes = new ScriptMeshes();
        var (center, radius) = FitCamera(scene);
        var clock = Stopwatch.StartNew();
        double lastTime = 0;
        KeyboardInput.Bind(window);
        TextInput.Bind(window);
        float scroll = 0f;
        window.OnScroll += (_, offset) => scroll += (float)offset.Y;
        // The mouse is measured from the corner of the 16:9 game view, as in the editor, so clicks land on the UI and
        // mouse rays line up with the picture when the window is letterboxed.
        MouseInput.Bind(window, () => GameArea(ctx, window), () =>
        {
            float amount = scroll;
            scroll = 0f;
            return amount;
        });
        var fullscreen = new FullscreenToggle();

        while (!window.ShouldClose)
        {
            Glfw.PollEvents();
            fullscreen.Update(window);
            if (ctx.IsMinimized)
            {
                Thread.Sleep(16);
                continue;
            }

            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(now - lastTime, 0.1);
            lastTime = now;
            SceneUpdate.Update(scene, dt);

            float aspect = VulkanContext.SceneAspect;
            var gameCam = FindCamera(scene);   // looked up every frame, since loading a scene can change the camera
            Matrix4x4 viewProj;
            Vector3 eye;
            if (gameCam is { } g)
            {
                eye = g.Obj.WorldMatrix.Translation;
                viewProj = g.Cam.View(g.Obj.WorldMatrix) *
                           VulkanMath.Projection(g.Cam.VerticalFov(aspect), aspect, g.Cam.Near, g.Cam.Far);
            }
            else
            {
                float angle = (float)clock.Elapsed.TotalSeconds * 0.4f;
                eye = center + new Vector3(MathF.Cos(angle) * radius, radius * 0.35f, MathF.Sin(angle) * radius);
                var view = Matrix4x4.CreateLookAt(eye, center, Vector3.UnitY);
                viewProj = view * VulkanMath.Projection(MathF.PI / 3f, aspect, 0.1f, radius * 10f + 100f);
            }

            var gameArea = GameArea(ctx, window);
            Input.SetView(new Vector2(gameArea.Z, gameArea.W), viewProj);   // for Input.GetMouseRay and the game's UI

            // The scene camera's anti-aliasing; only does work when the setting changes.
            ctx.ApplyAntiAliasing(gameCam?.Cam);

            // The sky, ambient light and every light besides the sun.
            var (environment, lights) = SceneLighting.Collect(scene, eye);
            renderer.SetLighting(environment, lights, _assets.GetImage);

            var sun = FindSun(scene);
            terrains.Sync(renderer, scene);
            scriptMeshes.Sweep(renderer);
            renderer.SetShadows(scene.Settings.Shadows && sun.Shadows, sun.ShadowStrength, sun.ShadowDistance, scene.Settings.ShadowResolution);
            renderer.BeginFrame(viewProj, eye, sun.Direction, sun.Radiance);
            foreach (var obj in scene.All)
                foreach (var mr in obj.Components.OfType<MeshRenderer>())
                {
                    if (mr.Mesh is { } scriptMesh)
                    {
                        renderer.DrawModel(scriptMeshes.KeyFor(renderer, scriptMesh), obj.WorldMatrix, mr.Tint, mr.Pose);
                        continue;
                    }
                    if (mr.Model.Length == 0) continue;
                    if (uploaded.Add(mr.Model))
                        renderer.UploadModel(mr.Model, _assets.GetModel(mr.Model));
                    renderer.DrawModel(mr.Model, obj.WorldMatrix, mr.Tint, mr.Pose);
                }
            terrains.Draw(renderer, scene);
            Particles.Draw(scene, renderer, eye);

            // The game's UI, in screen points over the game view; the canvas scales it to the framebuffer's pixels.
            uiCanvas.Begin();
            UiRenderer.Draw(scene, uiCanvas, gameArea);
            var points = new float2(Math.Max(window.Size.X, 1), Math.Max(window.Size.Y, 1));
            var pixelScale = new float2(window.FramebufferSize.X / points.X, window.FramebufferSize.Y / points.Y);
            renderer.EndFrame(cmd => uiCanvas.Render(cmd, points.X, points.Y, pixelScale));
        }

        ctx.WaitIdle();
        VulkanScripts.Release(scene);
    }

    // The 16:9 game view in the window's screen points: x, y, width, height.
    private static float4 GameArea(VulkanContext ctx, GlfwWindow window)
    {
        var vp = ctx.SceneViewport;
        return new float4(vp.X * window.Size.X, vp.Y * window.Size.Y, vp.Z * window.Size.X, vp.W * window.Size.Y);
    }

    // The scene's first camera and the object it sits on.
    private static (SceneObject Obj, CameraComponent Cam)? FindCamera(Scene scene)
    {
        foreach (var obj in scene.All)
            if (obj.Components.OfType<CameraComponent>().FirstOrDefault() is { } cam)
                return (obj, cam);
        return null;
    }

    // Uses the scene's first DirectionalLight, or a default sun so lighting always has a source.
    private static DirectionalLight FindSun(Scene scene) =>
        scene.All.SelectMany(o => o.Components.OfType<DirectionalLight>()).FirstOrDefault()
        ?? new DirectionalLight();

    private static (Vector3 Center, float Radius) FitCamera(Scene scene)
    {
        var positions = scene.All.Select(o => o.Position).ToList();
        if (positions.Count == 0) return (Vector3.Zero, 5f);

        var center = Vector3.Zero;
        foreach (var p in positions) center += p;
        center /= positions.Count;

        float maxDist = positions.Max(p => Vector3.Distance(p, center));
        return (center, Math.Max(4f, maxDist * 1.5f + 3f));
    }
}
