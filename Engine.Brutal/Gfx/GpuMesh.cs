using System.Numerics;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Engine.Core.Assets;

namespace Engine.Brutal.Gfx;

// One GPU mesh: interleaved vertices (position, normal, uv), a uint32 index buffer, and its material.
public sealed class GpuMesh : IDisposable
{
    public const int FloatsPerVertex = 12;   // position (3), normal (3), uv (2), color (4)
    private readonly Device _device;

    public VkBuffer VertexBuffer { get; }
    public VkDeviceMemory VertexMemory { get; }
    public VkBuffer IndexBuffer { get; }
    public VkDeviceMemory IndexMemory { get; }
    public int IndexCount { get; }
    public MaterialData Material { get; }
    public Matrix4x4 RestTransform { get; }     // where the mesh sits in its model when nothing animates it
    public int VertexCount { get; }
    public float[] TexCoords { get; }           // kept so an animated copy of the vertices can be written each frame
    public float[]? Colors { get; }             // vertex colors (4 per vertex), or null for white

    // The material's textures, as a descriptor set bound as set 1 of the model pipeline. Set by GpuMeshCache.
    public VkDescriptorSet MaterialSet { get; internal set; }

    private GpuMesh(Device device, VkBuffer vb, VkDeviceMemory vm, VkBuffer ib, VkDeviceMemory im,
        int indexCount, MaterialData material, Matrix4x4 restTransform, int vertexCount, float[] texCoords, float[]? colors)
    {
        _device = device;
        VertexBuffer = vb;
        VertexMemory = vm;
        IndexBuffer = ib;
        IndexMemory = im;
        IndexCount = indexCount;
        Material = material;
        RestTransform = restTransform;
        VertexCount = vertexCount;
        TexCoords = texCoords;
        Colors = colors;
    }

    public static GpuMesh Create(Device device, PhysicalDevice physical, MeshData mesh)
    {
        int vertexCount = mesh.Positions.Length / 3;
        var data = new float[vertexCount * FloatsPerVertex];
        for (int i = 0; i < vertexCount; i++)
        {
            int d = i * FloatsPerVertex, p = i * 3, t = i * 2;
            data[d + 0] = mesh.Positions[p + 0];
            data[d + 1] = mesh.Positions[p + 1];
            data[d + 2] = mesh.Positions[p + 2];
            data[d + 3] = mesh.Normals[p + 0];
            data[d + 4] = mesh.Normals[p + 1];
            data[d + 5] = mesh.Normals[p + 2];
            data[d + 6] = mesh.TexCoords[t + 0];
            data[d + 7] = mesh.TexCoords[t + 1];
            if (mesh.Colors is { } colors && colors.Length >= (i + 1) * 4)
            {
                data[d + 8] = colors[i * 4]; data[d + 9] = colors[i * 4 + 1]; data[d + 10] = colors[i * 4 + 2]; data[d + 11] = colors[i * 4 + 3];
            }
            else
            {
                data[d + 8] = 1f; data[d + 9] = 1f; data[d + 10] = 1f; data[d + 11] = 1f;
            }
        }

        var (vb, vm) = VkUtil.CreateHostBuffer(device, physical, (VkBufferUsageFlags)0x80, (ulong)(data.Length * sizeof(float)));
        device.WriteMemory<float>(data, vm, default, default);

        var (ib, im) = VkUtil.CreateHostBuffer(device, physical, (VkBufferUsageFlags)0x40, (ulong)(mesh.Indices.Length * sizeof(uint)));
        device.WriteMemory<uint>(mesh.Indices, im, default, default);

        return new GpuMesh(device, vb, vm, ib, im, mesh.Indices.Length, mesh.Material, mesh.RestTransform, vertexCount, mesh.TexCoords, mesh.Colors);
    }

    public void Dispose()
    {
        _device.DestroyBuffer(VertexBuffer, null);
        _device.FreeMemory(VertexMemory, null);
        _device.DestroyBuffer(IndexBuffer, null);
        _device.FreeMemory(IndexMemory, null);
    }
}

// Uploads each model once, keyed by its path relative to the content root. A model's meshes, the textures its
// materials use and the descriptor pool holding their material sets live and die together.
public sealed class GpuMeshCache : IDisposable
{
    private sealed class Entry
    {
        public required GpuMesh[] Meshes { get; init; }
        public required VkDescriptorPool Pool { get; init; }
        public required List<GpuTexture> Textures { get; init; }
    }

    private readonly VulkanContext _ctx;
    private readonly GpuMaterials _materials;
    private readonly Dictionary<string, Entry> _models = new(StringComparer.OrdinalIgnoreCase);

    public GpuMeshCache(VulkanContext ctx, GpuMaterials materials)
    {
        _ctx = ctx;
        _materials = materials;
    }

    public void Upload(string key, GlbModel model)
    {
        if (_models.ContainsKey(key)) return;
        _models[key] = Build(model);
    }

    // Swaps in a new version of a model, e.g. after the file changed on disk. The caller must make sure the GPU
    // is no longer using the old buffers (wait for the device to be idle first).
    public void Replace(string key, GlbModel model)
    {
        var fresh = Build(model);
        if (_models.Remove(key, out var old))
            Free(old);
        _models[key] = fresh;
    }

    private Entry Build(GlbModel model)
    {
        var textures = new List<GpuTexture>();
        var byImage = new Dictionary<ImageData, GpuTexture>(ReferenceEqualityComparer.Instance);
        var pool = _materials.CreatePool(Math.Max(1, model.Meshes.Count));

        // Each distinct image is uploaded once, however many materials use it.
        GpuTexture? Resolve(ImageData? image)
        {
            if (image is null) return null;
            if (byImage.TryGetValue(image, out var existing)) return existing;
            try
            {
                var texture = GpuTexture.Create(_ctx, image.BuildMips());
                byImage[image] = texture;
                textures.Add(texture);
                return texture;
            }
            catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException)
            {
                Console.WriteLine($"Could not upload texture {image.Name}: {ex.Message}");
                return null;
            }
        }

        var meshes = model.Meshes.Select(m =>
        {
            var mesh = GpuMesh.Create(_ctx.Device, _ctx.Physical, m);
            mesh.MaterialSet = _materials.CreateSet(pool, m.Material, Resolve);
            return mesh;
        }).ToArray();
        return new Entry { Meshes = meshes, Pool = pool, Textures = textures };
    }

    private void Free(Entry entry)
    {
        foreach (var mesh in entry.Meshes)
            mesh.Dispose();
        _ctx.Device.DestroyDescriptorPool(entry.Pool, null);
        foreach (var texture in entry.Textures)
            texture.Dispose();
    }

    public GpuMesh[] Get(string key) => _models[key].Meshes;

    // Frees a model. The caller must make sure the GPU is no longer using it (wait for the device to be idle first).
    public void Remove(string key)
    {
        if (_models.Remove(key, out var entry))
            Free(entry);
    }

    public void Dispose()
    {
        foreach (var entry in _models.Values)
            Free(entry);
        _models.Clear();
    }
}
