using System.Numerics;
using Engine.Core.Assets;

namespace Engine.Core.Animation;

// What an animated model looks like right now, for the renderer: one entry per mesh of the model (same order as
// GlbModel.Meshes). A skinned mesh has its bent vertices in Positions and Normals; any other mesh is drawn with its
// node's transform from MeshTransforms.
public sealed class ModelPose
{
    public required GlbModel Model { get; init; }
    public required Matrix4x4[] MeshTransforms { get; init; }
    public required float[]?[] Positions { get; init; }
    public required float[]?[] Normals { get; init; }

    // Texture coordinates per mesh, for a pose that moves them too (a terrain does). Null keeps each mesh's own.
    public float[]?[]? TexCoords { get; init; }

    // Vertex colors per mesh (4 floats per vertex), likewise. Null keeps each mesh's own.
    public float[]?[]? Colors { get; init; }

    // Ready-made vertices per mesh, in the engine's vertex layout: VertexFloats floats per vertex, position (3), normal
    // (3), texture coordinates (2) and color (4). When a mesh has them they are sent to the GPU as they are and the
    // arrays above aren't read for it. For big meshes changed every frame (water), filled on other threads.
    public float[]?[]? Vertices { get; init; }

    public const int VertexFloats = 12;
}

// One clip being played: how far along it is and how much it counts. Layers are applied in order, each blending over
// the result of the ones before it, which is how a fade from one animation into the next works.
internal readonly record struct PoseLayer(ClipBinding Clip, float Time, float Weight);

// A clip matched to a model's skeleton: channel i drives node Nodes[i] of that model (-1 when the model has no node
// of that name, and the channel is skipped). Clips from another file are matched by node name.
internal sealed class ClipBinding
{
    public required AnimationClipData Clip { get; init; }
    public required GlbModel Rig { get; init; }
    public required GlbModel Source { get; init; }
    public required int[] Nodes { get; init; }

    public static ClipBinding Create(GlbModel rig, GlbModel source, AnimationClipData clip)
    {
        var nodes = new int[clip.Channels.Count];
        Dictionary<string, int>? byName = null;
        for (int i = 0; i < nodes.Length; i++)
        {
            var channel = clip.Channels[i];
            if (ReferenceEquals(rig, source))
            {
                nodes[i] = channel.Node;
                continue;
            }
            byName ??= BuildNames(rig);
            nodes[i] = byName.GetValueOrDefault(channel.NodeName, -1);
        }
        return new ClipBinding { Clip = clip, Rig = rig, Source = source, Nodes = nodes };
    }

    private static Dictionary<string, int> BuildNames(GlbModel rig)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < rig.Nodes.Count; i++)
            names.TryAdd(rig.Nodes[i].Name, i);
        return names;
    }
}

internal static class PoseEvaluator
{
    // Works out the pose of a model with the layers applied over its rest pose. `previous` is reused when it fits.
    public static ModelPose Evaluate(GlbModel rig, ReadOnlySpan<PoseLayer> layers, ModelPose? previous)
    {
        int count = rig.Nodes.Count;
        var t = new Vector3[count];
        var r = new Quaternion[count];
        var s = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            t[i] = rig.Nodes[i].Translation;
            r[i] = rig.Nodes[i].Rotation;
            s[i] = rig.Nodes[i].Scale;
        }

        foreach (var layer in layers)
        {
            float w = Math.Clamp(layer.Weight, 0f, 1f);
            if (w <= 0f) continue;
            var channels = layer.Clip.Clip.Channels;
            for (int c = 0; c < channels.Count; c++)
            {
                int node = layer.Clip.Nodes[c];
                if (node < 0) continue;
                var channel = channels[c];
                switch (channel.Target)
                {
                    case ChannelTarget.Translation:
                        t[node] = Vector3.Lerp(t[node], SampleVector(channel, layer.Time), w);
                        break;
                    case ChannelTarget.Scale:
                        s[node] = Vector3.Lerp(s[node], SampleVector(channel, layer.Time), w);
                        break;
                    default:
                        r[node] = w >= 1f ? SampleRotation(channel, layer.Time) : Quaternion.Slerp(r[node], SampleRotation(channel, layer.Time), w);
                        break;
                }
            }
        }

        var world = new Matrix4x4[count];
        foreach (int i in rig.NodeOrder)
        {
            var local = Matrix4x4.CreateScale(s[i]) * Matrix4x4.CreateFromQuaternion(r[i]) * Matrix4x4.CreateTranslation(t[i]);
            int parent = rig.Nodes[i].Parent;
            world[i] = parent >= 0 ? local * world[parent] : local;
        }

        int meshCount = rig.Meshes.Count;
        bool reuse = previous is not null && ReferenceEquals(previous.Model, rig) && previous.MeshTransforms.Length == meshCount;
        var pose = reuse ? previous! : new ModelPose
        {
            Model = rig,
            MeshTransforms = new Matrix4x4[meshCount],
            Positions = new float[]?[meshCount],
            Normals = new float[]?[meshCount],
        };

        for (int m = 0; m < meshCount; m++)
        {
            var mesh = rig.Meshes[m];
            if (mesh.Skin < 0 || mesh.Joints is null || mesh.Weights is null)
            {
                pose.MeshTransforms[m] = mesh.Node >= 0 ? world[mesh.Node] : mesh.RestTransform;
                continue;
            }
            pose.MeshTransforms[m] = Matrix4x4.Identity;
            Skin(rig, mesh, world, pose, m);
        }
        return pose;
    }

    private static void Skin(GlbModel rig, MeshData mesh, Matrix4x4[] world, ModelPose pose, int index)
    {
        var skin = rig.Skins[mesh.Skin];
        var joints = new Matrix4x4[skin.Joints.Length];
        for (int j = 0; j < joints.Length; j++)
        {
            int node = skin.Joints[j];
            joints[j] = node >= 0 && node < world.Length ? skin.InverseBind[j] * world[node] : Matrix4x4.Identity;
        }

        int vertices = mesh.Positions.Length / 3;
        var positions = pose.Positions[index];
        if (positions is null || positions.Length != mesh.Positions.Length) positions = pose.Positions[index] = new float[mesh.Positions.Length];
        var normals = pose.Normals[index];
        if (normals is null || normals.Length != mesh.Normals.Length) normals = pose.Normals[index] = new float[mesh.Normals.Length];

        var jointIds = mesh.Joints!;
        var weights = mesh.Weights!;
        for (int v = 0; v < vertices; v++)
        {
            var blended = new Matrix4x4();
            float total = 0f;
            for (int k = 0; k < 4; k++)
            {
                float weight = weights[v * 4 + k];
                int joint = jointIds[v * 4 + k];
                if (weight <= 0f || joint >= joints.Length) continue;
                blended += joints[joint] * weight;   // Matrix4x4 * float scales every element
                total += weight;
            }

            var p = new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
            var n = new Vector3(mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]);
            if (total > 0f)
            {
                p = Vector3.Transform(p, blended * (1f / total));
                n = Vector3.TransformNormal(n, blended);
                float length = n.Length();
                if (length > 1e-6f) n /= length;
            }
            positions[v * 3] = p.X; positions[v * 3 + 1] = p.Y; positions[v * 3 + 2] = p.Z;
            normals[v * 3] = n.X; normals[v * 3 + 1] = n.Y; normals[v * 3 + 2] = n.Z;
        }
    }

    // The key at or before `time`, and how far toward the next one (0 to 1).
    private static (int Key, float Fraction) Locate(float[] times, float time)
    {
        if (time <= times[0]) return (0, 0f);
        if (time >= times[^1]) return (times.Length - 1, 0f);
        int lo = 0, hi = times.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (times[mid] <= time) lo = mid; else hi = mid;
        }
        float span = times[hi] - times[lo];
        return (lo, span > 1e-9f ? (time - times[lo]) / span : 0f);
    }

    private static Vector3 SampleVector(AnimationChannel c, float time)
    {
        var (key, f) = Locate(c.Times, time);
        switch (c.Interpolation)
        {
            case ChannelInterpolation.CubicSpline:
            {
                int next = Math.Min(key + 1, c.Times.Length - 1);
                if (next == key) return Read3(c.Values, key * 9 + 3);
                float dt = c.Times[next] - c.Times[key];
                return Hermite(Read3(c.Values, key * 9 + 3), Read3(c.Values, key * 9 + 6) * dt,
                    Read3(c.Values, next * 9 + 3), Read3(c.Values, next * 9) * dt, f);
            }
            case ChannelInterpolation.Step:
                return Read3(c.Values, key * 3);
            default:
                return f == 0f ? Read3(c.Values, key * 3) : Vector3.Lerp(Read3(c.Values, key * 3), Read3(c.Values, (key + 1) * 3), f);
        }
    }

    private static Quaternion SampleRotation(AnimationChannel c, float time)
    {
        var (key, f) = Locate(c.Times, time);
        switch (c.Interpolation)
        {
            case ChannelInterpolation.CubicSpline:
            {
                int next = Math.Min(key + 1, c.Times.Length - 1);
                if (next == key) return Read4(c.Values, key * 12 + 4);
                float dt = c.Times[next] - c.Times[key];
                var a = Read4(c.Values, key * 12 + 4);
                var b = Read4(c.Values, next * 12 + 4);
                var m0 = Read4(c.Values, key * 12 + 8) * dt;
                var m1 = Read4(c.Values, next * 12) * dt;
                float f2 = f * f, f3 = f2 * f;
                var q = a * (2 * f3 - 3 * f2 + 1) + m0 * (f3 - 2 * f2 + f) + b * (-2 * f3 + 3 * f2) + m1 * (f3 - f2);
                return Quaternion.Normalize(q);
            }
            case ChannelInterpolation.Step:
                return Read4(c.Values, key * 4);
            default:
                return f == 0f ? Read4(c.Values, key * 4) : Quaternion.Slerp(Read4(c.Values, key * 4), Read4(c.Values, (key + 1) * 4), f);
        }
    }

    private static Vector3 Read3(float[] v, int o) => new(v[o], v[o + 1], v[o + 2]);
    private static Quaternion Read4(float[] v, int o) => new(v[o], v[o + 1], v[o + 2], v[o + 3]);

    private static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return p0 * (2 * t3 - 3 * t2 + 1) + m0 * (t3 - 2 * t2 + t) + p1 * (-2 * t3 + 3 * t2) + m1 * (t3 - t2);
    }
}
