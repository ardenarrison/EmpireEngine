using System.Numerics;

namespace Engine.Core.Assets;

// One node of a model's skeleton and scene tree: its name, its parent (-1 for a root) and its resting pose.
public sealed record ModelNode(string Name, int Parent, Vector3 Translation, Quaternion Rotation, Vector3 Scale);

// A skin ties a mesh to the nodes (joints) that bend it. InverseBind[i] takes a vertex from the model's rest pose into
// joint i's own space.
public sealed record ModelSkin(int[] Joints, Matrix4x4[] InverseBind);

public enum ChannelTarget { Translation, Rotation, Scale }

public enum ChannelInterpolation { Linear, Step, CubicSpline }

// One animated property of one node: Values holds 3 floats per key for translation and scale and 4 for rotation (x, y, z, w).
// A cubic spline has three entries per key (in tangent, value, out tangent).
public sealed record AnimationChannel(int Node, string NodeName, ChannelTarget Target, ChannelInterpolation Interpolation,
    float[] Times, float[] Values);

// A named animation in a model file, such as "Walk" or "Idle".
public sealed record AnimationClipData(string Name, float Duration, IReadOnlyList<AnimationChannel> Channels);
