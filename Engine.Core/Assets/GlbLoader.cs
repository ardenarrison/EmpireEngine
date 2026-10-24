using System.Numerics;
using System.Text.Json;

namespace Engine.Core.Assets;

// A surface. The numbers are factors that multiply the matching texture when there is one, as in glTF: the base color
// factor times the base color map, roughness and metallic times the green and blue of the metallic-roughness map,
// emission times the emissive map. A missing map means 1 (white), or "no change" for the normal map.
public sealed record MaterialData(float[] BaseColor, float Roughness, float Metallic, float Ior,
    float SpecularFactor, float[] SpecularColor, float[] Emission,
    ImageData? BaseColorImage = null, ImageData? NormalImage = null, ImageData? MetallicRoughnessImage = null,
    ImageData? EmissiveImage = null, ImageData? OcclusionImage = null,
    float NormalScale = 1f, float OcclusionStrength = 1f,
    float AlphaCutoff = 0f,    // pixels with less alpha than this are not drawn; 0 means the surface is opaque
    bool AlphaBlend = false);  // see-through: blended over what is behind it by its alpha, drawn after solid surfaces

public sealed record MeshData(string Name, float[] Positions, float[] Normals, float[] TexCoords, uint[] Indices, MaterialData Material)
{
    // Where the mesh sits in the model when nothing is animating it: its node's transform. Identity for skinned
    // meshes (the skeleton positions those) and for models without animation, which are drawn as they always were.
    public Matrix4x4 RestTransform { get; init; } = Matrix4x4.Identity;

    public int Node { get; init; } = -1;    // index into the model's Nodes, or -1
    public int Skin { get; init; } = -1;    // index into the model's Skins, or -1 when the mesh is not skinned
    public ushort[]? Joints { get; init; }  // 4 per vertex: which of the skin's joints move the vertex
    public float[]? Weights { get; init; }  // 4 per vertex: how strongly each of them does
    public float[]? Colors { get; init; }   // 4 per vertex (red, green, blue, alpha): multiplies the material's color; null means white
}

public sealed class GlbModel
{
    public required string Name { get; init; }
    public required IReadOnlyList<MeshData> Meshes { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }

    // Skeleton and animation data (glTF only). Empty for models without any.
    public IReadOnlyList<ModelNode> Nodes { get; init; } = Array.Empty<ModelNode>();
    public IReadOnlyList<ModelSkin> Skins { get; init; } = Array.Empty<ModelSkin>();
    public IReadOnlyList<AnimationClipData> Animations { get; init; } = Array.Empty<AnimationClipData>();
    public int[] NodeOrder { get; init; } = Array.Empty<int>();   // node indices with every parent before its children

    // The animation with this name, or null.
    public AnimationClipData? FindAnimation(string name) =>
        Animations.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

// Reads binary glTF (.glb) and text glTF (.gltf). For .gltf, buffer 0 comes from a data: URI
// or from an external file next to the .gltf. Materials read their base color, normal, metallic-roughness, emissive and
// occlusion textures (embedded in the .glb, in a data: URI, or in files next to the model). Not handled yet: sparse
// accessors, texture transforms and morph targets. Models that have animations or skins also get their node
// hierarchy (each mesh sits at its node's transform, and skinned meshes keep their joint weights) and their animation
// clips, for the Animator component; models without them load exactly as before.
public static class GlbLoader
{
    private const uint Magic = 0x46546C67;      // "glTF"
    private const uint ChunkJson = 0x4E4F534A; // "JSON"
    private const uint ChunkBin = 0x004E4942;  // "BIN\0"

    public static GlbModel Load(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        byte[] jsonBytes;
        byte[] bin = Array.Empty<byte>();
        bool isBinary = file.Length >= 12 && BitConverter.ToUInt32(file, 0) == Magic;

        if (isBinary)
        {
            using var ms = new MemoryStream(file);
            using var br = new BinaryReader(ms);
            br.ReadUInt32();                                  // magic
            if (br.ReadUInt32() != 2) throw new NotSupportedException("Only glTF 2.0 is supported.");
            uint total = br.ReadUInt32();

            byte[]? json = null;
            while (ms.Position < total)
            {
                uint len = br.ReadUInt32();
                uint type = br.ReadUInt32();
                byte[] data = br.ReadBytes((int)len);
                if (type == ChunkJson) json = data;
                else if (type == ChunkBin) bin = data;
            }
            jsonBytes = json ?? throw new InvalidDataException("GLB has no JSON chunk.");
        }
        else
        {
            jsonBytes = file;
        }

        using JsonDocument doc = JsonDocument.Parse(jsonBytes);
        var gltf = doc.RootElement;
        if (!isBinary)
            bin = LoadFirstBuffer(gltf, dir);

        var images = new ImageReader(gltf, bin, dir);
        var meshes = new List<MeshData>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        var nodes = ReadNodes(gltf);
        var skins = ReadSkins(gltf, bin);
        var animations = ReadAnimations(gltf, bin, nodes);
        bool nodeAware = animations.Count > 0 || skins.Length > 0;
        var nodeOrder = NodeOrder(nodes);
        var restWorld = nodeAware ? RestWorldMatrices(nodes, nodeOrder) : Array.Empty<Matrix4x4>();

        void AddMesh(int meshIndex, int node, int skin)
        {
            var mesh = gltf.GetProperty("meshes")[meshIndex];
            string meshName = mesh.TryGetProperty("name", out var n) ? n.GetString() ?? "Mesh" : "Mesh";

            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                var attrs = prim.GetProperty("attributes");
                int posIdx = attrs.GetProperty("POSITION").GetInt32();
                var positions = ReadFloats(gltf, bin, posIdx);
                int vertexCount = positions.Length / 3;

                var normals = attrs.TryGetProperty("NORMAL", out var nIdx)
                    ? ReadFloats(gltf, bin, nIdx.GetInt32())
                    : new float[positions.Length];
                var uvs = attrs.TryGetProperty("TEXCOORD_0", out var tIdx)
                    ? ReadFloats(gltf, bin, tIdx.GetInt32())
                    : new float[vertexCount * 2];
                var indices = prim.TryGetProperty("indices", out var iIdx)
                    ? ReadIndices(gltf, bin, iIdx.GetInt32())
                    : Enumerable.Range(0, vertexCount).Select(i => (uint)i).ToArray();

                var data = new MeshData(meshName, positions, normals, uvs, indices, ReadMaterial(gltf, prim, images));
                bool skinned = nodeAware && skin >= 0 && skin < skins.Length &&
                               attrs.TryGetProperty("JOINTS_0", out _) && attrs.TryGetProperty("WEIGHTS_0", out _);
                if (skinned)
                {
                    data = new MeshData(meshName, positions, normals, uvs, indices, data.Material)
                    {
                        Node = node,
                        Skin = skin,
                        Joints = ReadJoints(gltf, bin, attrs.GetProperty("JOINTS_0").GetInt32()),
                        Weights = ReadFloats(gltf, bin, attrs.GetProperty("WEIGHTS_0").GetInt32()),
                    };
                }
                else if (nodeAware && node >= 0)
                {
                    data = new MeshData(meshName, positions, normals, uvs, indices, data.Material) { Node = node, RestTransform = restWorld[node] };
                }
                meshes.Add(data);

                var posAcc = gltf.GetProperty("accessors")[posIdx];
                if (posAcc.TryGetProperty("min", out var mn) && posAcc.TryGetProperty("max", out var mx))
                {
                    var lo = new Vector3(mn[0].GetSingle(), mn[1].GetSingle(), mn[2].GetSingle());
                    var hi = new Vector3(mx[0].GetSingle(), mx[1].GetSingle(), mx[2].GetSingle());
                    if (!skinned && nodeAware && node >= 0)
                        (lo, hi) = TransformBox(lo, hi, restWorld[node]);
                    min = Vector3.Min(min, lo);
                    max = Vector3.Max(max, hi);
                }
            }
        }

        if (gltf.TryGetProperty("meshes", out var meshArray))
        {
            if (!nodeAware)
            {
                for (int m = 0; m < meshArray.GetArrayLength(); m++)
                    AddMesh(m, -1, -1);
            }
            else if (gltf.TryGetProperty("nodes", out var nodeArray))
            {
                // One copy of a mesh for every node that uses it, each at its node.
                for (int i = 0; i < nodeArray.GetArrayLength(); i++)
                    if (nodeArray[i].TryGetProperty("mesh", out var meshRef))
                        AddMesh(meshRef.GetInt32(), i, nodeArray[i].TryGetProperty("skin", out var skinRef) ? skinRef.GetInt32() : -1);
            }
        }

        if (meshes.Count == 0) min = max = Vector3.Zero;

        return new GlbModel
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Meshes = meshes,
            BoundsMin = min,
            BoundsMax = max,
            Nodes = nodes,
            Skins = skins,
            Animations = animations,
            NodeOrder = nodeOrder,
        };
    }

    // ---- skeleton and animation ----------------------------------------------------------------

    // The names of a file's animations, read without loading anything else (meshes and textures stay on disk).
    // Empty for a file that has none or that can't be read.
    public static IReadOnlyList<string> ReadAnimationNames(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadJsonChunk(path));
            if (!doc.RootElement.TryGetProperty("animations", out var animations)) return Array.Empty<string>();
            var names = new List<string>();
            var used = new HashSet<string>();
            int index = 0;
            foreach (var animation in animations.EnumerateArray())
                names.Add(UniqueName(animation, index++, used));
            return names;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    // The JSON part of a .glb or .gltf file. Reads only that, so a big model costs no more than its header.
    private static byte[] ReadJsonChunk(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[20];
        int read = stream.Read(header, 0, header.Length);
        if (read >= 20 && BitConverter.ToUInt32(header, 0) == Magic)
        {
            if (BitConverter.ToUInt32(header, 16) != ChunkJson) throw new InvalidDataException("GLB has no JSON chunk first.");
            int length = (int)BitConverter.ToUInt32(header, 12);
            var json = new byte[length];
            stream.ReadExactly(json, 0, length);
            return json;
        }
        return File.ReadAllBytes(path);
    }

    // An animation's name, made unique within the file ("Animation 2" when it has none).
    private static string UniqueName(JsonElement animation, int index, HashSet<string> used)
    {
        string name = animation.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString())
            ? n.GetString()!.Trim()
            : $"Animation {index + 1}";
        string unique = name;
        for (int copy = 2; !used.Add(unique); copy++)
            unique = $"{name} ({copy})";
        return unique;
    }

    private static ModelNode[] ReadNodes(JsonElement gltf)
    {
        if (!gltf.TryGetProperty("nodes", out var array)) return Array.Empty<ModelNode>();
        int count = array.GetArrayLength();
        var parents = new int[count];
        Array.Fill(parents, -1);
        for (int i = 0; i < count; i++)
            if (array[i].TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray())
                    if (child.GetInt32() is var c && c >= 0 && c < count) parents[c] = i;

        var nodes = new ModelNode[count];
        for (int i = 0; i < count; i++)
        {
            var node = array[i];
            string name = node.TryGetProperty("name", out var n) ? n.GetString() ?? $"Node {i}" : $"Node {i}";
            var t = Vector3.Zero;
            var r = Quaternion.Identity;
            var s = Vector3.One;
            if (node.TryGetProperty("matrix", out var m) && m.GetArrayLength() == 16)
            {
                float[] f = new float[16];
                for (int k = 0; k < 16; k++) f[k] = m[k].GetSingle();
                // glTF matrices are column-major; filling a row-vector matrix in the same order gives the transpose we want.
                var matrix = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
                if (!Matrix4x4.Decompose(matrix, out s, out r, out t)) { t = matrix.Translation; r = Quaternion.Identity; s = Vector3.One; }
            }
            else
            {
                if (node.TryGetProperty("translation", out var tv) && tv.GetArrayLength() >= 3)
                    t = new Vector3(tv[0].GetSingle(), tv[1].GetSingle(), tv[2].GetSingle());
                if (node.TryGetProperty("rotation", out var rv) && rv.GetArrayLength() >= 4)
                    r = Quaternion.Normalize(new Quaternion(rv[0].GetSingle(), rv[1].GetSingle(), rv[2].GetSingle(), rv[3].GetSingle()));
                if (node.TryGetProperty("scale", out var sv) && sv.GetArrayLength() >= 3)
                    s = new Vector3(sv[0].GetSingle(), sv[1].GetSingle(), sv[2].GetSingle());
            }
            nodes[i] = new ModelNode(name, parents[i], t, r, s);
        }
        return nodes;
    }

    // Node indices with every parent listed before its children (a file with a loop in its tree stays valid: loops are cut).
    private static int[] NodeOrder(ModelNode[] nodes)
    {
        var order = new List<int>(nodes.Length);
        var state = new byte[nodes.Length];   // 0 not visited, 1 being visited, 2 done
        void Visit(int i)
        {
            if (state[i] != 0) return;
            state[i] = 1;
            int parent = nodes[i].Parent;
            if (parent >= 0 && state[parent] == 0) Visit(parent);
            state[i] = 2;
            order.Add(i);
        }
        for (int i = 0; i < nodes.Length; i++) Visit(i);
        return order.ToArray();
    }

    private static Matrix4x4[] RestWorldMatrices(ModelNode[] nodes, int[] order)
    {
        var world = new Matrix4x4[nodes.Length];
        foreach (int i in order)
        {
            var node = nodes[i];
            var local = Matrix4x4.CreateScale(node.Scale) * Matrix4x4.CreateFromQuaternion(node.Rotation) *
                        Matrix4x4.CreateTranslation(node.Translation);
            world[i] = node.Parent >= 0 ? local * world[node.Parent] : local;
        }
        return world;
    }

    private static (Vector3, Vector3) TransformBox(Vector3 lo, Vector3 hi, Matrix4x4 m)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = Vector3.Transform(new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z), m);
            min = Vector3.Min(min, corner);
            max = Vector3.Max(max, corner);
        }
        return (min, max);
    }

    private static ModelSkin[] ReadSkins(JsonElement gltf, byte[] bin)
    {
        if (!gltf.TryGetProperty("skins", out var array)) return Array.Empty<ModelSkin>();
        var skins = new List<ModelSkin>();
        foreach (var skin in array.EnumerateArray())
        {
            var joints = skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
            var inverse = new Matrix4x4[joints.Length];
            Array.Fill(inverse, Matrix4x4.Identity);
            if (skin.TryGetProperty("inverseBindMatrices", out var ibm))
            {
                var values = ReadFloats(gltf, bin, ibm.GetInt32());
                for (int i = 0; i < joints.Length && (i + 1) * 16 <= values.Length; i++)
                {
                    int o = i * 16;
                    inverse[i] = new Matrix4x4(values[o], values[o + 1], values[o + 2], values[o + 3],
                        values[o + 4], values[o + 5], values[o + 6], values[o + 7],
                        values[o + 8], values[o + 9], values[o + 10], values[o + 11],
                        values[o + 12], values[o + 13], values[o + 14], values[o + 15]);
                }
            }
            skins.Add(new ModelSkin(joints, inverse));
        }
        return skins.ToArray();
    }

    private static List<AnimationClipData> ReadAnimations(JsonElement gltf, byte[] bin, ModelNode[] nodes)
    {
        var clips = new List<AnimationClipData>();
        if (!gltf.TryGetProperty("animations", out var array)) return clips;

        var used = new HashSet<string>();
        int index = 0;
        foreach (var animation in array.EnumerateArray())
        {
            string name = UniqueName(animation, index++, used);
            var samplers = animation.GetProperty("samplers");
            var channels = new List<AnimationChannel>();
            float duration = 0f;

            foreach (var channel in animation.GetProperty("channels").EnumerateArray())
            {
                var target = channel.GetProperty("target");
                if (!target.TryGetProperty("node", out var nodeRef)) continue;
                int node = nodeRef.GetInt32();
                if (node < 0 || node >= nodes.Length) continue;
                var path = target.GetProperty("path").GetString() switch
                {
                    "translation" => (ChannelTarget?)ChannelTarget.Translation,
                    "rotation" => ChannelTarget.Rotation,
                    "scale" => ChannelTarget.Scale,
                    _ => null,   // morph target weights are not supported
                };
                if (path is null) continue;

                var sampler = samplers[channel.GetProperty("sampler").GetInt32()];
                var interpolation = (sampler.TryGetProperty("interpolation", out var ip) ? ip.GetString() : "LINEAR") switch
                {
                    "STEP" => ChannelInterpolation.Step,
                    "CUBICSPLINE" => ChannelInterpolation.CubicSpline,
                    _ => ChannelInterpolation.Linear,
                };
                var times = ReadFloats(gltf, bin, sampler.GetProperty("input").GetInt32());
                var values = ReadFloats(gltf, bin, sampler.GetProperty("output").GetInt32());
                int width = path == ChannelTarget.Rotation ? 4 : 3;
                int perKey = interpolation == ChannelInterpolation.CubicSpline ? width * 3 : width;
                if (times.Length == 0 || values.Length < times.Length * perKey) continue;   // a broken channel is skipped

                if (path == ChannelTarget.Rotation)
                    for (int k = 0; k < times.Length; k++)   // keep rotations unit length, so blending them is well behaved
                    {
                        int o = k * perKey + (interpolation == ChannelInterpolation.CubicSpline ? width : 0);
                        var q = Quaternion.Normalize(new Quaternion(values[o], values[o + 1], values[o + 2], values[o + 3]));
                        values[o] = q.X; values[o + 1] = q.Y; values[o + 2] = q.Z; values[o + 3] = q.W;
                    }

                duration = MathF.Max(duration, times[^1]);
                channels.Add(new AnimationChannel(node, nodes[node].Name, path.Value, interpolation, times, values));
            }
            clips.Add(new AnimationClipData(name, duration, channels));
        }
        return clips;
    }

    private static ushort[] ReadJoints(JsonElement gltf, byte[] bin, int accessorIndex)
    {
        var acc = gltf.GetProperty("accessors")[accessorIndex];
        int type = acc.GetProperty("componentType").GetInt32();
        if (type != 5121 && type != 5123) throw new NotSupportedException($"Unsupported joint index type {type}.");
        int size = type == 5121 ? 1 : 2;
        int count = acc.GetProperty("count").GetInt32();
        var view = gltf.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
        int offset = Opt(view, "byteOffset", 0) + Opt(acc, "byteOffset", 0);
        int stride = Opt(view, "byteStride", 4 * size);

        var result = new ushort[count * 4];
        for (int i = 0; i < count; i++)
            for (int c = 0; c < 4; c++)
            {
                int p = offset + i * stride + c * size;
                result[i * 4 + c] = size == 1 ? bin[p] : BitConverter.ToUInt16(bin, p);
            }
        return result;
    }

    // Material for a primitive. Without a material, the glTF defaults are used for metallic/roughness
    // and a white base color (roughness 0.5, metallic 0 so unlabeled models still look reasonable).
    private static MaterialData ReadMaterial(JsonElement gltf, JsonElement prim, ImageReader images)
    {
        ImageData? baseColorImage = null, normalImage = null, metallicRoughnessImage = null, emissiveImage = null, occlusionImage = null;
        float normalScale = 1f, occlusionStrength = 1f, alphaCutoff = 0f;
        bool alphaBlend = false;
        var baseColor = new float[] { 1, 1, 1, 1 };
        var specColor = new float[] { 1, 1, 1 };
        float roughness = 0.5f, metallic = 0f, ior = 1.5f, specFactor = 1f;
        var emission = new float[] { 0, 0, 0 };
        float emissiveStrength = 1f;

        if (prim.TryGetProperty("material", out var matIdx) && gltf.TryGetProperty("materials", out var mats))
        {
            var mat = mats[matIdx.GetInt32()];
            roughness = 1f;                                  // glTF spec defaults
            metallic = 1f;

            if (mat.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                if (pbr.TryGetProperty("baseColorFactor", out var factor) && factor.GetArrayLength() >= 4)
                    for (int i = 0; i < 4; i++)
                        baseColor[i] = factor[i].GetSingle();
                if (pbr.TryGetProperty("roughnessFactor", out var r)) roughness = r.GetSingle();
                if (pbr.TryGetProperty("metallicFactor", out var m)) metallic = m.GetSingle();
                baseColorImage = images.Texture(pbr, "baseColorTexture");
                metallicRoughnessImage = images.Texture(pbr, "metallicRoughnessTexture");
            }

            normalImage = images.Texture(mat, "normalTexture");
            if (normalImage is not null && mat.GetProperty("normalTexture").TryGetProperty("scale", out var ns))
                normalScale = ns.GetSingle();
            emissiveImage = images.Texture(mat, "emissiveTexture");
            occlusionImage = images.Texture(mat, "occlusionTexture");
            if (occlusionImage is not null && mat.GetProperty("occlusionTexture").TryGetProperty("strength", out var os))
                occlusionStrength = os.GetSingle();

            // MASK cuts out pixels below the cutoff; BLEND is see-through, blended by its alpha.
            string alphaMode = mat.TryGetProperty("alphaMode", out var am) ? am.GetString() ?? "OPAQUE" : "OPAQUE";
            float cutoff = mat.TryGetProperty("alphaCutoff", out var ac) ? ac.GetSingle() : 0.5f;
            if (alphaMode == "MASK") alphaCutoff = Math.Max(cutoff, 0.001f);
            else if (alphaMode == "BLEND") alphaBlend = true;

            if (mat.TryGetProperty("emissiveFactor", out var emissive) && emissive.GetArrayLength() >= 3)
                for (int i = 0; i < 3; i++)
                    emission[i] = emissive[i].GetSingle();

            if (mat.TryGetProperty("extensions", out var ext))
            {
                if (ext.TryGetProperty("KHR_materials_emissive_strength", out var strengthExt) &&
                    strengthExt.TryGetProperty("emissiveStrength", out var strength))
                {
                    emissiveStrength = strength.GetSingle();
                }
                if (ext.TryGetProperty("KHR_materials_ior", out var iorExt) &&
                    iorExt.TryGetProperty("ior", out var iorVal))
                {
                    ior = iorVal.GetSingle();
                }
                if (ext.TryGetProperty("KHR_materials_specular", out var specExt))
                {
                    if (specExt.TryGetProperty("specularFactor", out var sf)) specFactor = sf.GetSingle();
                    if (specExt.TryGetProperty("specularColorFactor", out var scf) && scf.GetArrayLength() >= 3)
                        for (int i = 0; i < 3; i++)
                            specColor[i] = scf[i].GetSingle();
                }
            }
        }
        // Emission = emissiveFactor * KHR_materials_emissive_strength (strength defaults to 1).
        for (int i = 0; i < 3; i++)
            emission[i] *= emissiveStrength;

        return new MaterialData(baseColor, roughness, metallic, ior, specFactor, specColor, emission,
            baseColorImage, normalImage, metallicRoughnessImage, emissiveImage, occlusionImage,
            normalScale, occlusionStrength, alphaCutoff, alphaBlend);
    }

    // Decodes the file's images on first use, so each is read once however many materials share it.
    private sealed class ImageReader(JsonElement gltf, byte[] bin, string dir)
    {
        private readonly Dictionary<int, ImageData?> _decoded = new();

        // The image a texture reference points at, e.g. material["normalTexture"] = { "index": 3 }. Null when the
        // material has no such texture or the image can't be read (the material then draws without it).
        public ImageData? Texture(JsonElement owner, string property)
        {
            if (!owner.TryGetProperty(property, out var info) || !info.TryGetProperty("index", out var indexElement)) return null;
            if (!gltf.TryGetProperty("textures", out var textures)) return null;
            int index = indexElement.GetInt32();
            if (index < 0 || index >= textures.GetArrayLength()) return null;
            return textures[index].TryGetProperty("source", out var source) ? Image(source.GetInt32()) : null;
        }

        private ImageData? Image(int index)
        {
            if (_decoded.TryGetValue(index, out var cached)) return cached;
            ImageData? image = null;
            try
            {
                image = Decode(index);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or KeyNotFoundException
                                           or InvalidOperationException or FormatException or ArgumentException)
            {
                Console.WriteLine($"Could not read texture {index}: {ex.Message}");
            }
            _decoded[index] = image;
            return image;
        }

        private ImageData Decode(int index)
        {
            var element = gltf.GetProperty("images")[index];
            string name = element.TryGetProperty("name", out var n) ? n.GetString() ?? $"image{index}" : $"image{index}";

            if (element.TryGetProperty("bufferView", out var viewIndex))
            {
                var view = gltf.GetProperty("bufferViews")[viewIndex.GetInt32()];
                int offset = Opt(view, "byteOffset", 0), length = view.GetProperty("byteLength").GetInt32();
                return ImageData.Decode(bin.AsSpan(offset, length).ToArray(), name);
            }

            string uri = element.GetProperty("uri").GetString() ?? "";
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return ImageData.Decode(Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]), name);

            string path = Path.Combine(dir, Uri.UnescapeDataString(uri));
            return ImageData.Decode(File.ReadAllBytes(path), Path.GetFileName(path));
        }
    }

    private static byte[] LoadFirstBuffer(JsonElement gltf, string dir)
    {
        if (!gltf.TryGetProperty("buffers", out var buffers) || buffers.GetArrayLength() == 0)
            return Array.Empty<byte>();
        var first = buffers[0];
        if (!first.TryGetProperty("uri", out var uriEl))
            return Array.Empty<byte>();

        string uri = uriEl.GetString() ?? "";
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = uri.IndexOf(',');
            return Convert.FromBase64String(uri[(comma + 1)..]);
        }
        return File.ReadAllBytes(Path.Combine(dir, Uri.UnescapeDataString(uri)));
    }

    // Reads an accessor as floats. Integer data counts only when the accessor says it is normalized (a 0..1 or -1..1 value).
    private static float[] ReadFloats(JsonElement gltf, byte[] bin, int accessorIndex)
    {
        var acc = gltf.GetProperty("accessors")[accessorIndex];
        int type = acc.GetProperty("componentType").GetInt32();
        bool normalized = acc.TryGetProperty("normalized", out var nz) && nz.GetBoolean();
        int size = type switch
        {
            5126 => 4,
            5120 or 5121 when normalized => 1,
            5122 or 5123 when normalized => 2,
            _ => throw new NotSupportedException("Only float vertex attributes (or normalized integers) are supported yet."),
        };

        int count = acc.GetProperty("count").GetInt32();
        int comps = acc.GetProperty("type").GetString() switch
        {
            "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16,
            var t => throw new NotSupportedException($"Unsupported accessor type {t}"),
        };

        var view = gltf.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
        int offset = Opt(view, "byteOffset", 0) + Opt(acc, "byteOffset", 0);
        int stride = Opt(view, "byteStride", comps * size);

        var result = new float[count * comps];
        for (int i = 0; i < count; i++)
            for (int c = 0; c < comps; c++)
            {
                int p = offset + i * stride + c * size;
                result[i * comps + c] = type switch
                {
                    5126 => BitConverter.ToSingle(bin, p),
                    5120 => MathF.Max((sbyte)bin[p] / 127f, -1f),
                    5121 => bin[p] / 255f,
                    5122 => MathF.Max(BitConverter.ToInt16(bin, p) / 32767f, -1f),
                    _ => BitConverter.ToUInt16(bin, p) / 65535f,
                };
            }
        return result;
    }

    private static uint[] ReadIndices(JsonElement gltf, byte[] bin, int accessorIndex)
    {
        var acc = gltf.GetProperty("accessors")[accessorIndex];
        int ctype = acc.GetProperty("componentType").GetInt32();
        int size = ctype switch
        {
            5121 => 1, 5123 => 2, 5125 => 4,
            _ => throw new NotSupportedException($"Unsupported index type {ctype}"),
        };

        int count = acc.GetProperty("count").GetInt32();
        var view = gltf.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
        int offset = Opt(view, "byteOffset", 0) + Opt(acc, "byteOffset", 0);

        var result = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int p = offset + i * size;
            result[i] = size switch
            {
                1 => bin[p],
                2 => BitConverter.ToUInt16(bin, p),
                _ => BitConverter.ToUInt32(bin, p),
            };
        }
        return result;
    }

    private static int Opt(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;
}
