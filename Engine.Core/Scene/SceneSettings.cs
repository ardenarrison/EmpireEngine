namespace Engine.Core.Scene;

// Settings that belong to a whole scene rather than to one object. They are saved in the scene file and edited by
// clicking the scene in the editor's Assets panel. The scene's gravity is the other scene-wide setting; it lives on
// the scene's physics (Scene.Physics.Gravity, or Physics.Gravity from a script) and is saved alongside these.
public sealed class SceneSettings
{
    public const int DefaultShadowResolution = 2048;

    // The shadow map sizes to choose from. Bigger gives sharper shadows and costs more GPU memory and time:
    // 4096 x 4096 uses 64 MB for each of the two frames in flight.
    public static readonly int[] ShadowResolutions = { 512, 1024, 2048, 4096 };

    private int _shadowResolution = DefaultShadowResolution;

    // False turns off shadows for the whole scene, whatever the sun's own Shadows setting says.
    public bool Shadows { get; set; } = true;

    // Width and height of the sun's shadow map, in texels. Any number is rounded to the nearest of ShadowResolutions.
    public int ShadowResolution
    {
        get => _shadowResolution;
        set => _shadowResolution = Nearest(value);
    }

    public void CopyFrom(SceneSettings other)
    {
        _shadowResolution = other._shadowResolution;
        Shadows = other.Shadows;
    }

    private static int Nearest(int value) =>
        ShadowResolutions.OrderBy(r => Math.Abs(r - value)).ThenByDescending(r => r).First();
}
