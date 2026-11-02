using System.Numerics;

namespace Engine.Core.Assets;

// The flat, unlit rectangle a SpriteRenderer is drawn with: a picture's size in world units at a given pixels-per-unit,
// centered on the object and facing +Z.
public static class SpriteMesh
{
    public static GlbModel Build(ImageData image, float pixelsPerUnit, bool flipX = false, bool flipY = false)
    {
        float ppu = MathF.Max(pixelsPerUnit, 0.01f);
        float hw = image.Width / ppu * 0.5f, hh = image.Height / ppu * 0.5f;

        // Half a texel in from each edge, so filtering never reaches round to the opposite side of the picture.
        float u0 = 0.5f / image.Width, u1 = 1f - u0, v0 = 0.5f / image.Height, v1 = 1f - v0;
        if (flipX) (u0, u1) = (u1, u0);
        if (flipY) (v0, v1) = (v1, v0);

        float[] positions = [-hw, -hh, 0, hw, -hh, 0, hw, hh, 0, -hw, hh, 0];
        float[] normals = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1];
        float[] uvs = [u0, v1, u1, v1, u1, v0, u0, v0];   // picture rows run top to bottom
        uint[] indices = [0, 1, 2, 0, 2, 3];
        var material = new MaterialData([1, 1, 1, 1], 1f, 0f, 1.5f, 0f, [1, 1, 1], [0, 0, 0],
            BaseColorImage: image, AlphaBlend: image.HasTransparency, Unlit: true);
        return new GlbModel
        {
            Name = image.Name,
            Meshes = [new MeshData(image.Name, positions, normals, uvs, indices, material)],
            BoundsMin = new Vector3(-hw, -hh, 0),
            BoundsMax = new Vector3(hw, hh, 0),
        };
    }
}
