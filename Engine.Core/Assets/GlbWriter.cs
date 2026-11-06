using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Engine.Core.Assets;

// Writes binary glTF (.glb). UpdateVertices changes the vertices of an existing file and leaves everything else in it as it
// was: materials, textures, animations, even what this engine doesn't read. Write makes a new file from a loaded model, for
// models that have no .glb to change. Positions and normals are given per mesh, in the same order as the model's Meshes.
public static class GlbWriter
{
    private const uint Magic = 0x46546C67;      // "glTF"
    private const uint ChunkJson = 0x4E4F534A; // "JSON"
    private const uint ChunkBin = 0x004E4942;  // "BIN\0"

    private const int ComponentFloat = 5126;
    private const int ComponentUInt = 5125;
    private const int TargetVertices = 34962;
    private const int TargetIndices = 34963;

    // The .glb file with these meshes' vertices in it. The meshes must come from loading that file. Throws NotSupportedException
    // when the file keeps them in a form that can't be written back, and InvalidDataException when it doesn't match the meshes
    // (for example it was exported again since).
    public static byte[] UpdateVertices(byte[] glb, IReadOnlyList<MeshData> meshes, IReadOnlyList<float[]> positions, IReadOnlyList<float[]> normals)
    {
        var chunks = ReadChunks(glb);
        int jsonChunk = chunks.FindIndex(c => c.Type == ChunkJson);
        int binChunk = chunks.FindIndex(c => c.Type == ChunkBin);
        if (jsonChunk < 0 || binChunk < 0)
            throw new NotSupportedException("The file keeps its vertices outside the .glb.");

        var root = JsonNode.Parse(chunks[jsonChunk].Data)?.AsObject() ?? throw new InvalidDataException("The file has no glTF data.");
        byte[] bin = chunks[binChunk].Data;
        for (int m = 0; m < meshes.Count; m++)
        {
            if (meshes[m].PositionAccessor < 0)
                throw new InvalidDataException("The model didn't come from a glTF file.");
            WriteAccessor(root, bin, meshes[m].PositionAccessor, positions[m]);
            SetBounds(root, meshes[m].PositionAccessor, positions[m]);
            if (meshes[m].NormalAccessor >= 0)
                WriteAccessor(root, bin, meshes[m].NormalAccessor, normals[m]);
        }

        chunks[jsonChunk] = (ChunkJson, Encoding.UTF8.GetBytes(root.ToJsonString()));
        return Assemble(chunks);
    }

    // A new .glb holding the model's meshes and materials, with these vertices. Each mesh is written where it sits in the model.
    public static byte[] Write(GlbModel model, IReadOnlyList<float[]> positions, IReadOnlyList<float[]> normals)
    {
        var file = new NewFile();
        for (int m = 0; m < model.Meshes.Count; m++)
            file.AddMesh(model.Meshes[m], positions[m], normals[m]);
        return Assemble([(ChunkJson, Encoding.UTF8.GetBytes(file.ToJson())), (ChunkBin, file.Binary)]);
    }

    private static List<(uint Type, byte[] Data)> ReadChunks(byte[] glb)
    {
        if (glb.Length < 12 || BitConverter.ToUInt32(glb, 0) != Magic)
            throw new NotSupportedException("The file isn't a .glb.");
        var chunks = new List<(uint, byte[])>();
        for (int at = 12; at + 8 <= glb.Length;)
        {
            int length = (int)BitConverter.ToUInt32(glb, at);
            uint type = BitConverter.ToUInt32(glb, at + 4);
            if (length < 0 || at + 8 + length > glb.Length)
                throw new InvalidDataException("The .glb is cut short.");
            chunks.Add((type, glb.AsSpan(at + 8, length).ToArray()));
            at += 8 + length;
        }
        return chunks;
    }

    private static byte[] Assemble(IReadOnlyList<(uint Type, byte[] Data)> chunks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(2u);
        writer.Write(0u);   // the length, filled in below
        foreach (var (type, data) in chunks)
        {
            int padding = (4 - data.Length % 4) % 4;   // every chunk ends on 4 bytes: spaces for JSON, zeros otherwise
            writer.Write((uint)(data.Length + padding));
            writer.Write(type);
            writer.Write(data);
            for (int i = 0; i < padding; i++)
                writer.Write(type == ChunkJson ? (byte)' ' : (byte)0);
        }
        writer.Flush();
        byte[] result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)result.Length);
        return result;
    }

    // Writes three floats per vertex over an accessor's data, which may share its buffer view with other attributes.
    private static void WriteAccessor(JsonObject root, byte[] bin, int index, float[] values)
    {
        var accessor = root["accessors"]?[index]?.AsObject() ?? throw new InvalidDataException("The file no longer matches the model.");
        if (accessor["sparse"] is not null || accessor["bufferView"] is null)
            throw new NotSupportedException("The file stores its vertices as changes to other data.");
        if (accessor["componentType"]?.GetValue<int>() != ComponentFloat || accessor["type"]?.GetValue<string>() != "VEC3")
            throw new NotSupportedException("The file stores its vertices compressed.");
        int count = accessor["count"]?.GetValue<int>() ?? 0;
        if (count * 3 != values.Length)
            throw new InvalidDataException("The file no longer matches the model: it has a different number of vertices.");

        var view = root["bufferViews"]?[accessor["bufferView"]!.GetValue<int>()]?.AsObject() ?? throw new InvalidDataException("The file is damaged.");
        if ((view["buffer"]?.GetValue<int>() ?? 0) != 0)
            throw new NotSupportedException("The file keeps its vertices outside the .glb.");
        int offset = (view["byteOffset"]?.GetValue<int>() ?? 0) + (accessor["byteOffset"]?.GetValue<int>() ?? 0);
        int stride = view["byteStride"]?.GetValue<int>() ?? 12;
        if (count > 0 && offset + (count - 1) * stride + 12 > bin.Length)
            throw new InvalidDataException("The file is damaged.");

        for (int i = 0; i < count; i++)
            for (int c = 0; c < 3; c++)
                BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(offset + i * stride + c * 4), values[i * 3 + c]);
    }

    // glTF requires the bounds of positions, and the engine sizes a model by them.
    private static void SetBounds(JsonObject root, int index, float[] positions)
    {
        var accessor = root["accessors"]![index]!.AsObject();
        var (min, max) = Bounds(positions);
        accessor["min"] = Vector(min);
        accessor["max"] = Vector(max);
    }

    private static (Vector3 Min, Vector3 Max) Bounds(float[] positions)
    {
        if (positions.Length < 3) return (Vector3.Zero, Vector3.Zero);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i + 2 < positions.Length; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    private static JsonArray Vector(Vector3 v) => [v.X, v.Y, v.Z];

    // The parts of a .glb being made from scratch.
    private sealed class NewFile
    {
        private readonly MemoryStream _bin = new();
        private readonly JsonArray _views = [], _accessors = [], _meshes = [], _nodes = [], _materials = [], _textures = [], _images = [];
        private readonly Dictionary<MaterialData, int> _materialIds = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ImageData, int> _textureIds = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _extensions = [];

        public byte[] Binary => _bin.ToArray();

        public void AddMesh(MeshData mesh, float[] positions, float[] normals)
        {
            // Files without animation have no node transforms that the loader reads, so the mesh's own goes into its vertices.
            var (placed, turned) = mesh.RestTransform.IsIdentity ? (positions, normals) : Bake(mesh.RestTransform, positions, normals);
            var (min, max) = Bounds(placed);
            var attributes = new JsonObject
            {
                ["POSITION"] = Floats(placed, "VEC3", 3, TargetVertices, min, max),
                ["NORMAL"] = Floats(turned, "VEC3", 3, TargetVertices),
            };
            if (mesh.TexCoords.Length > 0) attributes["TEXCOORD_0"] = Floats(mesh.TexCoords, "VEC2", 2, TargetVertices);
            if (mesh.Colors is { } colors) attributes["COLOR_0"] = Floats(colors, "VEC4", 4, TargetVertices);

            _meshes.Add(new JsonObject
            {
                ["name"] = mesh.Name,
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = attributes,
                    ["indices"] = Indices(mesh.Indices),
                    ["material"] = Material(mesh.Material),
                }),
            });
            _nodes.Add(new JsonObject { ["mesh"] = _meshes.Count - 1, ["name"] = mesh.Name });
        }

        public string ToJson()
        {
            var nodeIds = new JsonArray();
            for (int i = 0; i < _nodes.Count; i++) nodeIds.Add(i);
            var root = new JsonObject
            {
                ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Empire Engine" },
                ["scene"] = 0,
                ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = nodeIds }),
                ["nodes"] = _nodes,
                ["meshes"] = _meshes,
                ["materials"] = _materials,
                ["accessors"] = _accessors,
                ["bufferViews"] = _views,
                ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = _bin.Length }),
            };
            if (_images.Count > 0)
            {
                root["images"] = _images;
                root["textures"] = _textures;
            }
            if (_extensions.Count > 0)
            {
                var used = new JsonArray();
                foreach (var extension in _extensions) used.Add(extension);
                root["extensionsUsed"] = used;
            }
            return root.ToJsonString();
        }

        private static (float[] Positions, float[] Normals) Bake(Matrix4x4 transform, float[] positions, float[] normals)
        {
            var placed = new float[positions.Length];
            var turned = new float[normals.Length];
            for (int i = 0; i + 2 < positions.Length; i += 3)
            {
                var p = Vector3.Transform(new Vector3(positions[i], positions[i + 1], positions[i + 2]), transform);
                var n = Vector3.TransformNormal(new Vector3(normals[i], normals[i + 1], normals[i + 2]), transform);
                if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n);
                (placed[i], placed[i + 1], placed[i + 2]) = (p.X, p.Y, p.Z);
                (turned[i], turned[i + 1], turned[i + 2]) = (n.X, n.Y, n.Z);
            }
            return (placed, turned);
        }

        private int View(ReadOnlySpan<byte> bytes, int? target)
        {
            while (_bin.Length % 4 != 0) _bin.WriteByte(0);
            var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = _bin.Length, ["byteLength"] = bytes.Length };
            if (target is { } t) view["target"] = t;
            _bin.Write(bytes);
            _views.Add(view);
            return _views.Count - 1;
        }

        private int Floats(float[] values, string type, int components, int target, Vector3? min = null, Vector3? max = null)
        {
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            var accessor = new JsonObject
            {
                ["bufferView"] = View(bytes, target),
                ["componentType"] = ComponentFloat,
                ["count"] = values.Length / components,
                ["type"] = type,
            };
            if (min is { } low && max is { } high)
            {
                accessor["min"] = Vector(low);
                accessor["max"] = Vector(high);
            }
            _accessors.Add(accessor);
            return _accessors.Count - 1;
        }

        private int Indices(uint[] indices)
        {
            var bytes = new byte[indices.Length * 4];
            Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
            _accessors.Add(new JsonObject
            {
                ["bufferView"] = View(bytes, TargetIndices),
                ["componentType"] = ComponentUInt,
                ["count"] = indices.Length,
                ["type"] = "SCALAR",
            });
            return _accessors.Count - 1;
        }

        private int Material(MaterialData material)
        {
            if (_materialIds.TryGetValue(material, out int known)) return known;

            var pbr = new JsonObject
            {
                ["baseColorFactor"] = new JsonArray(material.BaseColor.Select(v => (JsonNode?)v).ToArray()),
                ["metallicFactor"] = material.Metallic,
                ["roughnessFactor"] = material.Roughness,
            };
            if (Texture(material.BaseColorImage) is { } baseColor) pbr["baseColorTexture"] = new JsonObject { ["index"] = baseColor };
            if (Texture(material.MetallicRoughnessImage) is { } metallicRoughness) pbr["metallicRoughnessTexture"] = new JsonObject { ["index"] = metallicRoughness };

            var result = new JsonObject
            {
                ["pbrMetallicRoughness"] = pbr,
                ["emissiveFactor"] = new JsonArray(material.Emission.Select(v => (JsonNode?)v).ToArray()),
            };
            if (Texture(material.NormalImage) is { } normal) result["normalTexture"] = new JsonObject { ["index"] = normal, ["scale"] = material.NormalScale };
            if (Texture(material.EmissiveImage) is { } emissive) result["emissiveTexture"] = new JsonObject { ["index"] = emissive };
            if (Texture(material.OcclusionImage) is { } occlusion) result["occlusionTexture"] = new JsonObject { ["index"] = occlusion, ["strength"] = material.OcclusionStrength };
            if (material.AlphaBlend)
                result["alphaMode"] = "BLEND";
            else if (material.AlphaCutoff > 0f)
            {
                result["alphaMode"] = "MASK";
                result["alphaCutoff"] = material.AlphaCutoff;
            }

            _extensions.Add("KHR_materials_ior");
            _extensions.Add("KHR_materials_specular");
            result["extensions"] = new JsonObject
            {
                ["KHR_materials_ior"] = new JsonObject { ["ior"] = material.Ior },
                ["KHR_materials_specular"] = new JsonObject
                {
                    ["specularFactor"] = material.SpecularFactor,
                    ["specularColorFactor"] = new JsonArray(material.SpecularColor.Select(v => (JsonNode?)v).ToArray()),
                },
            };

            _materials.Add(result);
            return _materialIds[material] = _materials.Count - 1;
        }

        private int? Texture(ImageData? image)
        {
            if (image?.Pixels is null) return null;   // HDR pictures don't go in a PNG
            if (_textureIds.TryGetValue(image, out int known)) return known;
            _images.Add(new JsonObject { ["bufferView"] = View(Png(image), null), ["mimeType"] = "image/png" });
            _textures.Add(new JsonObject { ["source"] = _images.Count - 1 });
            return _textureIds[image] = _textures.Count - 1;
        }
    }

    // An RGBA picture as a PNG.
    private static byte[] Png(ImageData image)
    {
        int row = image.Width * 4;
        var raw = new byte[(row + 1) * image.Height];   // each row starts with its filter type, 0 for none
        for (int y = 0; y < image.Height; y++)
            Buffer.BlockCopy(image.Pixels!, y * row, raw, y * (row + 1) + 1, row);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;   // bits per channel
        header[9] = 6;   // red, green, blue, alpha

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(png, "IHDR", header);
        WritePngChunk(png, "IDAT", compressed.ToArray());
        WritePngChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body, 4);
        png.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(body));
        png.Write(number);
    }

    private static uint Crc(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
