using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Brutal.Gfx;
using Engine.Brutal.Rendering;
using Engine.Core.Assets;

namespace Engine.Brutal.Metal;

// An image on the GPU with its full mip chain, like GpuTexture: 8-bit images as RGBA8 and HDR images as RGBA16Float, with colors
// used exactly as stored.
internal sealed class MetalTexture : IDisposable
{
    public nint Handle { get; }
    public int MipLevels { get; }
    public bool IsHdr { get; }

    private MetalTexture(nint handle, int mipLevels, bool hdr)
    {
        Handle = handle;
        MipLevels = mipLevels;
        IsHdr = hdr;
    }

    public static MetalTexture Solid(nint device, byte r, byte g, byte b, byte a) =>
        Create(device, [new ImageLevel(1, 1, [r, g, b, a], null)]);

    public static MetalTexture Create(nint device, IReadOnlyList<ImageLevel> levels)
    {
        bool hdr = levels[0].FloatPixels is not null;
        nint texture = MetalGpu.Texture(device, hdr ? MetalGpu.FormatRgba16F : MetalGpu.FormatRgba8, levels[0].Width, levels[0].Height, levels.Count);
        for (int i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            if (hdr)
            {
                var floats = level.FloatPixels!;
                var halves = new Half[floats.Length];
                for (int p = 0; p < floats.Length; p++)
                    halves[p] = (Half)Math.Clamp(floats[p], 0f, 65000f);
                MetalGpu.Upload(texture, i, level.Width, level.Height, MemoryMarshal.AsBytes<Half>(halves), 8);
            }
            else
            {
                MetalGpu.Upload(texture, i, level.Width, level.Height, level.Pixels!, 4);
            }
        }
        return new MetalTexture(texture, levels.Count, hdr);
    }

    public void Dispose() => ObjC.Release(Handle);
}

// One mesh on the GPU: interleaved vertices (see SceneShading), a uint32 index buffer, its material and the material's five textures
// (base color, normal, metallic-roughness, emissive, occlusion; plain stand-ins where the material has none).
internal sealed class MetalMesh : IDisposable
{
    public required nint VertexBuffer { get; init; }
    public required nint IndexBuffer { get; init; }
    public required int IndexCount { get; init; }
    public required int VertexCount { get; init; }
    public required MaterialData Material { get; init; }
    public required Matrix4x4 RestTransform { get; init; }
    public required float[] TexCoords { get; init; }
    public float[]? Colors { get; init; }
    public required nint[] Textures { get; init; }

    public void Dispose()
    {
        ObjC.Release(VertexBuffer);
        ObjC.Release(IndexBuffer);
    }
}

// Uploads each model once, keyed by its path relative to the content root, like GpuMeshCache. A model's meshes and the textures its
// materials use live and die together. A command buffer keeps what it uses alive, so freeing never has to wait for the GPU.
internal sealed class MetalMeshCache : IDisposable
{
    private sealed record Entry(MetalMesh[] Meshes, List<MetalTexture> Textures);

    private readonly nint _device;
    private readonly MetalTexture _white;
    private readonly MetalTexture _flatNormal;
    private readonly Dictionary<string, Entry> _models = new(StringComparer.OrdinalIgnoreCase);

    // Trilinear filtering with repeat wrapping: how glTF samples by default.
    public nint Sampler { get; }

    public MetalMeshCache(nint device)
    {
        _device = device;
        _white = MetalTexture.Solid(device, 255, 255, 255, 255);
        _flatNormal = MetalTexture.Solid(device, 128, 128, 255, 255);
        Sampler = MetalGpu.Sampler(device, linear: true, mipmaps: true, repeatU: true, repeatV: true);
    }

    public void Upload(string key, GlbModel model)
    {
        if (_models.ContainsKey(key)) return;
        _models[key] = Build(model);
    }

    public void Replace(string key, GlbModel model)
    {
        var fresh = Build(model);
        Remove(key);
        _models[key] = fresh;
    }

    public MetalMesh[] Get(string key) => _models[key].Meshes;

    public void Remove(string key)
    {
        if (_models.Remove(key, out var entry))
            Free(entry);
    }

    private Entry Build(GlbModel model)
    {
        var textures = new List<MetalTexture>();
        var byImage = new Dictionary<ImageData, MetalTexture>(ReferenceEqualityComparer.Instance);

        // Each distinct image is uploaded once, however many materials use it.
        nint Resolve(ImageData? image, MetalTexture fallback)
        {
            if (image is null) return fallback.Handle;
            if (byImage.TryGetValue(image, out var existing)) return existing.Handle;
            try
            {
                var texture = MetalTexture.Create(_device, image.BuildMips());
                byImage[image] = texture;
                textures.Add(texture);
                return texture.Handle;
            }
            catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException)
            {
                Console.WriteLine($"Could not upload texture {image.Name}: {ex.Message}");
                return fallback.Handle;
            }
        }

        var meshes = model.Meshes.Select(m =>
        {
            var vertices = SceneShading.RestVertices(m);
            nint vb = MetalGpu.Buffer(_device, vertices.Length * sizeof(float));
            MetalGpu.Write<float>(vb, vertices);
            nint ib = MetalGpu.Buffer(_device, m.Indices.Length * sizeof(uint));
            MetalGpu.Write<uint>(ib, m.Indices);
            var material = m.Material;
            return new MetalMesh
            {
                VertexBuffer = vb,
                IndexBuffer = ib,
                IndexCount = m.Indices.Length,
                VertexCount = m.Positions.Length / 3,
                Material = material,
                RestTransform = m.RestTransform,
                TexCoords = m.TexCoords,
                Colors = m.Colors,
                Textures =
                [
                    Resolve(material.BaseColorImage, _white),
                    Resolve(material.NormalImage, _flatNormal),
                    Resolve(material.MetallicRoughnessImage, _white),
                    Resolve(material.EmissiveImage, _white),
                    Resolve(material.OcclusionImage, _white),
                ],
            };
        }).ToArray();
        return new Entry(meshes, textures);
    }

    private static void Free(Entry entry)
    {
        foreach (var mesh in entry.Meshes)
            mesh.Dispose();
        foreach (var texture in entry.Textures)
            texture.Dispose();
    }

    public void Dispose()
    {
        foreach (var entry in _models.Values)
            Free(entry);
        _models.Clear();
        _white.Dispose();
        _flatNormal.Dispose();
        ObjC.Release(Sampler);
    }
}
