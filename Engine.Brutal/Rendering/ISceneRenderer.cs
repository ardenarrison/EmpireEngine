using Engine.Brutal.Gfx;
using Engine.Brutal.Metal;
using Engine.Core.Assets;
using Engine.Core.Rendering;

namespace Engine.Brutal.Rendering;

// The scene renderer the editor and games draw with: BrutalRenderer with Vulkan, MetalRenderer with Metal.
public interface ISceneRenderer : IRenderer
{
    // Replaces an uploaded model with a new version. Waits for the GPU first, so call it between frames.
    void ReloadModel(string key, GlbModel model);

    // Frees uploaded models that will not be drawn again. Waits for the GPU first, so call it between frames.
    void UnloadModels(IReadOnlyCollection<string> keys);

    // Ends the frame like EndFrame(), then drawUi draws 2D (such as the game's UI) over the finished scene.
    void EndFrame(Action<UiPass>? drawUi);
}

public static class SceneRenderers
{
    // The scene renderer for a context's graphics API.
    public static ISceneRenderer Create(IGraphicsContext ctx, ShaderLibrary shaders) => ctx switch
    {
        VulkanContext vulkan => new BrutalRenderer(vulkan, shaders),
        MetalContext metal => new MetalRenderer(metal, shaders),
        _ => throw new NotSupportedException($"There is no scene renderer for {ctx.GetType().Name}."),
    };
}
