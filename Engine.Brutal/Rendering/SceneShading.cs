using System.Numerics;
using Engine.Core.Animation;
using Engine.Core.Assets;
using Engine.Core.Rendering;
using Engine.Core.Scene;

namespace Engine.Brutal.Rendering;

// What the scene renderers for Vulkan (BrutalRenderer) and Metal (MetalRenderer) work out the same way: the data the model and sky
// shaders read, where the sun's shadow camera goes, and the vertices of posed meshes. Only how it reaches the GPU differs.
internal static class SceneShading
{
    public const int VertexStride = 48;     // 12 floats: position, normal, uv, color
    public const int PushBytes = 256;       // mvp(64) model(64) baseColor(16) material(16) camera(16) specular(16) light(16) lightColor(16) emission(16) extra(16); many GPUs cap push constants at 256
    public const int PushFloats = PushBytes / sizeof(float);
    public const int OffModel = 16, OffColor = 32, OffMaterial = 36, OffCamera = 40, OffSpecular = 44, OffLight = 48, OffRadiance = 52, OffEmission = 56;
    public const int OffExtra = 60;
    public const int MaxLights = SceneLighting.MaxLights;
    public const int FrameLightVp = 24 + MaxLights * 16;     // where the sun's view-projection sits in the frame block
    public const int FrameShadow = FrameLightVp + 16;        // shadow settings, after the sun matrix
    public const int FrameFog = FrameShadow + 4;             // fog distances, then its color
    public const int FrameFloats = FrameFog + 8;             // ambient, sky parameters and colors, the lights, the sun matrix, shadows, fog
    public const int MaxPanoramaWidth = 4096;                // wider sky images are shrunk, which looks the same
    public const float MinShadowDistance = 5f;
    public const float MaxShadowDistance = 500f;

    // The camera position, the direction toward the sun and its radiance, which stay the same for every model in a frame.
    public static void WriteFrameConstants(float[] push, Vector3 cameraPosition, Vector3 sunDirection, Vector3 sunRadiance)
    {
        push[OffCamera + 0] = cameraPosition.X;
        push[OffCamera + 1] = cameraPosition.Y;
        push[OffCamera + 2] = cameraPosition.Z;
        push[OffCamera + 3] = 0;
        push[OffLight + 0] = sunDirection.X;
        push[OffLight + 1] = sunDirection.Y;
        push[OffLight + 2] = sunDirection.Z;
        push[OffLight + 3] = 0;
        push[OffRadiance + 0] = sunRadiance.X;
        push[OffRadiance + 1] = sunRadiance.Y;
        push[OffRadiance + 2] = sunRadiance.Z;
        push[OffRadiance + 3] = 0;
    }

    // One mesh's matrices and material, written into the push constants.
    public static void WriteMeshConstants(float[] push, Matrix4x4 world, Matrix4x4 viewProjection, Vector4 tint, MaterialData material)
    {
        WriteMatrix(push, 0, world * viewProjection);
        WriteMatrix(push, OffModel, world);
        push[OffColor + 0] = tint.X * material.BaseColor[0];
        push[OffColor + 1] = tint.Y * material.BaseColor[1];
        push[OffColor + 2] = tint.Z * material.BaseColor[2];
        push[OffColor + 3] = tint.W * material.BaseColor[3];
        push[OffMaterial + 0] = material.Roughness;
        push[OffMaterial + 1] = material.Metallic;
        push[OffMaterial + 2] = material.Ior;
        push[OffMaterial + 3] = material.AlphaCutoff;
        push[OffSpecular + 0] = material.SpecularColor[0];
        push[OffSpecular + 1] = material.SpecularColor[1];
        push[OffSpecular + 2] = material.SpecularColor[2];
        push[OffSpecular + 3] = material.SpecularFactor;
        push[OffEmission + 0] = material.Emission[0];
        push[OffEmission + 1] = material.Emission[1];
        push[OffEmission + 2] = material.Emission[2];
        push[OffEmission + 3] = 0;
        push[OffExtra + 0] = material.OcclusionImage is null ? 0f : material.OcclusionStrength;
        push[OffExtra + 1] = material.NormalImage is null ? 0f : MathF.Max(material.NormalScale, 0.0001f);
        push[OffExtra + 2] = material.Unlit ? 1f : 0f;
    }

    // Where a mesh goes in the world: its node's transform (the posed one while animated) then the object's.
    public static Matrix4x4 MeshWorld(Matrix4x4 world, ModelPose? pose, Matrix4x4 restTransform, int index) =>
        (pose is { } p && index < p.MeshTransforms.Length ? p.MeshTransforms[index] : restTransform) * world;

    // True when a mesh is see-through in a draw: its material blends, or the tint or the material color isn't fully opaque.
    public static bool IsTransparent(Vector4 tint, MaterialData material) =>
        material.AlphaBlend || tint.W * material.BaseColor[3] < 0.999f;

    // The sun's camera for shadows: a box shadowDistance wide on each side of a point a little ahead of the viewer, so the shadows
    // near the camera and the area it looks at are covered. The box moves in whole shadow texels, so shadow edges stay still instead
    // of crawling as the camera moves. Row-vector convention, like DrawModel. texelDepth is one shadow texel in shadow-depth units.
    public static Matrix4x4 LightViewProjection(Vector3 sunDirection, Matrix4x4 viewProjection, Vector3 eye, float shadowDistance,
        int shadowSize, out float texelDepth)
    {
        float extent = shadowDistance;
        var towardSun = sunDirection.LengthSquared() > 1e-12f ? Vector3.Normalize(sunDirection) : Vector3.UnitY;
        var up = MathF.Abs(towardSun.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        // Which way the viewer looks, from the inverse of their view-projection (the middle of the near and far planes).
        var forward = -Vector3.UnitZ;
        if (Matrix4x4.Invert(viewProjection, out var inverse))
        {
            var near = Vector4.Transform(new Vector4(0f, 0f, ClipDepth.Near, 1f), inverse);
            var far = Vector4.Transform(new Vector4(0f, 0f, ClipDepth.Ahead, 1f), inverse);
            var direction = new Vector3(far.X, far.Y, far.Z) / far.W - new Vector3(near.X, near.Y, near.Z) / near.W;
            if (direction.LengthSquared() > 1e-12f && float.IsFinite(direction.X)) forward = Vector3.Normalize(direction);
        }
        var center = eye + forward * (extent * 0.5f);

        // Snap the center to the shadow map's texel grid, measured in the sun's view.
        var orientation = Matrix4x4.CreateLookAt(Vector3.Zero, -towardSun, up);
        var inSunView = Vector3.Transform(center, orientation);
        float texel = extent * 2f / shadowSize;
        inSunView.X = MathF.Round(inSunView.X / texel) * texel;
        inSunView.Y = MathF.Round(inSunView.Y / texel) * texel;
        Matrix4x4.Invert(orientation, out var fromSunView);
        center = Vector3.Transform(inSunView, fromSunView);

        // The sun's camera sits well back, so tall things between it and the area still cast shadows into it.
        float back = extent * 2f + 50f;
        var view = Matrix4x4.CreateLookAt(center + towardSun * back, center, up);
        var projection = Matrix4x4.CreateOrthographic(extent * 2f, extent * 2f, 0.1f, back * 2f);
        texelDepth = texel / (back * 2f - 0.1f);
        return view * projection;
    }

    // What the frame block of the model and sky shaders holds, in its order. panoramaMips and panoramaHdr describe the loaded sky
    // image, or are 1 and false when there is none (hasPanorama false).
    public readonly record struct ShadowSettings(bool On, float Strength, float TexelDepth, float Distance, int Size);

    public static void WriteFrameData(float[] d, EnvironmentData e, IReadOnlyList<LightData> lights, bool hasPanorama, int panoramaMips,
        bool panoramaHdr, Matrix4x4 lightViewProjection, ShadowSettings shadow)
    {
        d[0] = e.Ambient.X; d[1] = e.Ambient.Y; d[2] = e.Ambient.Z; d[3] = 0;

        // A panorama that hasn't loaded (or failed to) falls back to the flat sky color.
        bool panorama = e.Sky == SkyMode.Panorama && hasPanorama;
        int count = Math.Min(lights.Count, MaxLights);
        d[4] = panorama ? 2 : e.Sky == SkyMode.Gradient ? 1 : 0;
        d[5] = e.SkyIntensity;
        d[6] = count;
        d[7] = panoramaMips;
        d[8] = panorama && panoramaHdr ? 1 : 0;
        d[9] = e.SkyLighting;
        d[10] = 0; d[11] = 0;
        d[12] = e.Zenith.X; d[13] = e.Zenith.Y; d[14] = e.Zenith.Z; d[15] = 0;
        d[16] = e.Horizon.X; d[17] = e.Horizon.Y; d[18] = e.Horizon.Z; d[19] = 0;
        d[20] = e.Ground.X; d[21] = e.Ground.Y; d[22] = e.Ground.Z; d[23] = 0;

        for (int i = 0; i < count; i++)
        {
            var l = lights[i];
            int o = 24 + i * 16;
            var at = l.Type == LightType.Directional ? l.Direction : l.Position;
            d[o + 0] = at.X; d[o + 1] = at.Y; d[o + 2] = at.Z; d[o + 3] = (int)l.Type;
            d[o + 4] = l.Radiance.X; d[o + 5] = l.Radiance.Y; d[o + 6] = l.Radiance.Z; d[o + 7] = l.Range;
            d[o + 8] = l.Direction.X; d[o + 9] = l.Direction.Y; d[o + 10] = l.Direction.Z; d[o + 11] = l.CosInner;
            d[o + 12] = l.CosOuter; d[o + 13] = 0; d[o + 14] = 0; d[o + 15] = 0;
        }
        WriteMatrix(d, FrameLightVp, lightViewProjection);
        d[FrameShadow] = shadow.On ? shadow.Strength : 0f;
        d[FrameShadow + 1] = shadow.TexelDepth;
        d[FrameShadow + 2] = shadow.Distance * 2f / shadow.Size;
        d[FrameShadow + 3] = 0;
        d[FrameFog] = e.FogStart; d[FrameFog + 1] = e.FogEnd; d[FrameFog + 2] = e.Fog ? 1 : 0; d[FrameFog + 3] = 0;
        d[FrameFog + 4] = e.FogColor.X; d[FrameFog + 5] = e.FogColor.Y; d[FrameFog + 6] = e.FogColor.Z; d[FrameFog + 7] = 0;
    }

    // The vertices of one posed mesh in the GPU's layout, or null when the pose doesn't fit the mesh (it is then drawn as it rests).
    // A pose with ready-made vertices is used as it is; otherwise its positions, normals, texture coordinates and colors are put
    // together in a scratch array kept per vertex count, across the CPU's cores for big meshes.
    public static float[]? PosedVertices(ModelPose pose, int index, int vertexCount, float[] restTexCoords, float[]? restColors,
        Dictionary<int, float[]> scratchByCount)
    {
        if (pose.Vertices is { } ready && index < ready.Length && ready[index] is { } vertices &&
            vertices.Length == vertexCount * Gfx.GpuMesh.FloatsPerVertex)
            return vertices;

        var positions = pose.Positions[index];
        var normals = pose.Normals[index];
        var colors = pose.Colors is { } ownColors && index < ownColors.Length && ownColors[index] is { } movedColors && movedColors.Length == vertexCount * 4
            ? movedColors : restColors;
        var texCoords = pose.TexCoords is { } own && index < own.Length && own[index] is { } moved && moved.Length == restTexCoords.Length
            ? moved : restTexCoords;
        if (positions is null || normals is null || positions.Length != vertexCount * 3 || normals.Length != positions.Length)
            return null;
        if (!scratchByCount.TryGetValue(vertexCount, out var scratch))
            scratchByCount[vertexCount] = scratch = new float[vertexCount * Gfx.GpuMesh.FloatsPerVertex];
        Interleave(scratch, vertexCount, positions, normals, texCoords, colors);
        return scratch;
    }

    // Puts separate vertex arrays together in the GPU's layout: position, normal, texture coordinates, color. Big meshes
    // are done in chunks across the CPU's cores.
    public static void Interleave(float[] data, int vertexCount, float[] positions, float[] normals, float[] texCoords, float[]? colors)
    {
        int chunks = vertexCount >= 32768 ? Environment.ProcessorCount * 4 : 1;
        int chunkSize = (vertexCount + chunks - 1) / chunks;
        Parallel.For(0, chunks, chunk =>
        {
            for (int v = chunk * chunkSize, end = Math.Min(vertexCount, v + chunkSize); v < end; v++)
            {
                int d = v * Gfx.GpuMesh.FloatsPerVertex, p = v * 3, t = v * 2;
                data[d] = positions[p]; data[d + 1] = positions[p + 1]; data[d + 2] = positions[p + 2];
                data[d + 3] = normals[p]; data[d + 4] = normals[p + 1]; data[d + 5] = normals[p + 2];
                data[d + 6] = texCoords[t]; data[d + 7] = texCoords[t + 1];
                if (colors is not null)
                {
                    data[d + 8] = colors[v * 4]; data[d + 9] = colors[v * 4 + 1]; data[d + 10] = colors[v * 4 + 2]; data[d + 11] = colors[v * 4 + 3];
                }
                else
                {
                    data[d + 8] = 1f; data[d + 9] = 1f; data[d + 10] = 1f; data[d + 11] = 1f;
                }
            }
        });
    }

    // The interleaved vertices of a mesh as it rests (see Interleave).
    public static float[] RestVertices(MeshData mesh)
    {
        int vertexCount = mesh.Positions.Length / 3;
        var data = new float[vertexCount * Gfx.GpuMesh.FloatsPerVertex];
        var colors = mesh.Colors is { } c && c.Length >= vertexCount * 4 ? c : null;
        Interleave(data, vertexCount, mesh.Positions, mesh.Normals, mesh.TexCoords, colors);
        return data;
    }

    // Row-major System.Numerics fields in order. GLSL reads these as column-major (the transpose),
    // so "mvp * v" in the shader matches "v * mvp" in C#.
    public static void WriteMatrix(float[] dst, int offset, Matrix4x4 m)
    {
        dst[offset + 0] = m.M11; dst[offset + 1] = m.M12; dst[offset + 2] = m.M13; dst[offset + 3] = m.M14;
        dst[offset + 4] = m.M21; dst[offset + 5] = m.M22; dst[offset + 6] = m.M23; dst[offset + 7] = m.M24;
        dst[offset + 8] = m.M31; dst[offset + 9] = m.M32; dst[offset + 10] = m.M33; dst[offset + 11] = m.M34;
        dst[offset + 12] = m.M41; dst[offset + 13] = m.M42; dst[offset + 14] = m.M43; dst[offset + 15] = m.M44;
    }
}
