using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Engine.Brutal.Gfx;
using Engine.Core.Assets;

namespace Engine.Brutal.Ui;

// The screen every game opens with, before any scene loads or script runs: the Empire Engine logo, then the BRUTAL logo
// and its copyright notice. The BRUTAL License v1.0 (section 2.2) requires the BRUTAL logo to be shown in every copy of a
// product built with BRUTAL before gameplay begins, unaltered except for scaling, for long enough to read, and no less
// prominently than other parties' logos. So:
//   - the logo is the official one (Logos/BrutalLogo.png, see its README), drawn at its own proportions;
//   - it is drawn wider than, and as tall as, Empire Engine's logo above it;
//   - it can't be skipped, and time only counts on frames that are drawn, so minimizing the window doesn't use it up.
public static class SplashScreen
{
    public const float FadeInSeconds = 0.5f;
    public const float HoldSeconds = 2.5f;
    public const float FadeOutSeconds = 0.5f;
    public const float TotalSeconds = FadeInSeconds + HoldSeconds + FadeOutSeconds;

    // Layout in points for a 1280 x 720 window; everything grows or shrinks with the window.
    private const float CrownHeight = 120f;
    private const float TitleSize = 44f;
    private const float BrutalWidth = 600f;     // wider than the Empire Engine logo above it (the crown and its name, about 340)

    private static readonly float4 Background = new(0.055f, 0.06f, 0.075f, 1f);

    private const string BuiltWith = "built with";
    private const string NoticeLine1 = "BRUTAL game development framework by Ahwoo Limited";
    private const string NoticeLine2 = "Copyright (c) 2024 Ahwoo Limited  |  ahwoo.com/brutal";

    // Shows the splash and returns when it is over, or when the window is closed (check window.ShouldClose afterwards).
    // small is a canvas with a small font (the game's UI one) for the captions.
    public static void Run(VulkanContext ctx, ShaderLibrary shaders, GlfwWindow window, Canvas2D small)
    {
        float unit = Math.Clamp(Math.Min(window.Size.X / 1280f, window.Size.Y / 720f), 0.5f, 3f);
        using var big = new Canvas2D(ctx, shaders, Canvas2D.DefaultFont(), TitleSize * unit, window.ContentScale.X);
        int crown = AddImage(big, "EmpireEngineCrown.png");
        int brutal = AddImage(big, "BrutalLogo.png");
        const float crownAspect = 512f / 410f, brutalAspect = 2048f / 448f;

        var frame = Stopwatch.StartNew();
        double last = 0, shown = 0;
        while (!window.ShouldClose && shown < TotalSeconds)
        {
            Glfw.PollEvents();
            double now = frame.Elapsed.TotalSeconds;
            double delta = Math.Min(now - last, 0.1);
            last = now;
            if (ctx.IsMinimized)
            {
                Thread.Sleep(16);
                continue;
            }
            shown += delta;

            float w = Math.Max(window.Size.X, 1), h = Math.Max(window.Size.Y, 1);
            var pixelScale = new float2(window.FramebufferSize.X / w, window.FramebufferSize.Y / h);
            float fade = Fade((float)shown);
            var white = new float4(0.96f, 0.96f, 0.98f, fade);
            var dim = new float4(0.62f, 0.64f, 0.70f, fade);

            // Top to bottom: crown, name, a gap, "built with", the BRUTAL logo, its notice. Centered as a block.
            float crownH = CrownHeight * unit, crownW = crownH * crownAspect;
            float titleH = big.FontSize;
            float brutalW = BrutalWidth * unit, brutalH = brutalW / brutalAspect;
            float captionH = small.FontSize;
            float gap = 14f * unit, bigGap = 52f * unit;
            float blockH = crownH + gap + titleH + bigGap + captionH + gap + brutalH + gap * 1.5f + captionH * 2f + 4f;
            float y = (h - blockH) / 2f;

            big.Begin();
            small.Begin();
            big.Rect(0, 0, w, h, Background);

            big.Image(crown, (w - crownW) / 2f, y, crownW, crownH, new float4(1f, 1f, 1f, fade));
            y += crownH + gap;
            const string title = "EMPIRE ENGINE";
            big.Text(title, (w - big.MeasureText(title)) / 2f, y, white);
            y += titleH + bigGap;

            small.Text(BuiltWith, (w - small.MeasureText(BuiltWith)) / 2f, y, dim);
            y += captionH + gap;
            big.Image(brutal, (w - brutalW) / 2f, y, brutalW, brutalH, new float4(1f, 1f, 1f, fade));
            y += brutalH + gap * 1.5f;
            small.Text(NoticeLine1, (w - small.MeasureText(NoticeLine1)) / 2f, y, dim);
            small.Text(NoticeLine2, (w - small.MeasureText(NoticeLine2)) / 2f, y + captionH + 2f, dim);

            ctx.BeginFrame();
            ctx.EndFrame(cmd =>
            {
                big.Render(cmd, w, h, pixelScale);
                small.Render(cmd, w, h, pixelScale);
            });
        }
    }

    // 0 to 1 and back: eased in over FadeInSeconds, full while held, eased out over FadeOutSeconds.
    public static float Fade(float seconds)
    {
        float t = seconds < FadeInSeconds ? seconds / FadeInSeconds
            : seconds > FadeInSeconds + HoldSeconds ? 1f - (seconds - FadeInSeconds - HoldSeconds) / FadeOutSeconds
            : 1f;
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static int AddImage(Canvas2D canvas, string file)
    {
        using var stream = typeof(SplashScreen).Assembly.GetManifestResourceStream("Engine.Brutal.Ui.Logos." + file)
            ?? throw new InvalidOperationException($"{file} is missing from Engine.Brutal.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var image = ImageData.Decode(copy.ToArray(), file);
        return canvas.AddImage(image.Pixels ?? throw new InvalidDataException($"{file} has no 8-bit pixels."), image.Width, image.Height);
    }
}
