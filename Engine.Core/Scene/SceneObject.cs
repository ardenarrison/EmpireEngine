using System.Numerics;

namespace Engine.Core.Scene;

// Something that can be put on an object. The engine's own components (Mesh, Rigidbody, lights...) are data the
// engine acts on. A script can define its own component by extending Component instead of Behaviour:
//
//     public class Bobber : Component
//     {
//         public float Height = 0.5f;                 // public fields show in the Inspector, like a script's
//         public override void Update() => Object.Position += new Vector3(0, MathF.Sin(Time.time) * Height * Time.deltaTime, 0);
//     }
//
// It then appears under its own name in Add Component, and Start and Update run while the game plays.
public abstract class Component
{
    // The object a component written in a script is on. Set when the game starts; null for the engine's own components.
    public SceneObject Object { get; internal set; } = null!;

    // The same as Object.
    public SceneObject sceneObject => Object;

    // Destroys an object and everything under it, at the end of this frame or after delay seconds (see Scene.Destroy).
    public void Destroy(SceneObject? target, float delay = 0f) => Scene.Destroy(target, delay);

    // Another component on the same object, e.g. GetComponent<MeshRenderer>(). Null when there is none.
    public T? GetComponent<T>() where T : Component => Object?.Components.OfType<T>().FirstOrDefault();

    // Called once when the object is destroyed with Destroy, just before it leaves the scene, for components written in
    // scripts that had started.
    public virtual void OnDestroy() { }

    // Called once before the first Update, for components written in scripts.
    public virtual void Start() { }

    // Called every frame while the game plays, for components written in scripts.
    public virtual void Update() { }
}

public sealed class MeshRenderer : Component
{
    public string Model { get; set; } = "";              // path relative to Content/
    public Vector4 Tint { get; set; } = Vector4.One;

    // A mesh made by a script, drawn in place of Model while it is set (water, generated shapes). Runtime only: it is
    // never saved. Set Pose as well to move its vertices from frame to frame without making a new mesh.
    public Assets.GlbModel? Mesh { get; set; }

    // How an Animator has posed the model right now. Runtime only: it is never saved, and null means the resting pose.
    public Animation.ModelPose? Pose { get; set; }
}

// A script's way into an object's position, rotation and scale (local to its parent). Get it from
// sceneObject.transform; changing a value here moves the object.
public sealed class Transform
{
    private readonly SceneObject _owner;

    internal Transform(SceneObject owner) => _owner = owner;

    public Vector3 position { get => _owner.Position; set => _owner.Position = value; }

    // The same position at double precision. In a project set to Float positions this is the float value, widened.
    public Vector3D positionD { get => _owner.PositionD; set => _owner.PositionD = value; }

    // Where the object is in the world, worked out in double precision through its parents (read only).
    public Vector3D worldPositionD => _owner.WorldPositionD;
    public Quaternion rotation { get => _owner.Rotation; set => _owner.Rotation = value; }
    public Vector3 scale { get => _owner.Scale; set => _owner.Scale = value; }
}

public sealed class SceneObject
{
    public Guid Id { get; }
    public string Name { get; set; } = "GameObject";

    public SceneObject? Parent { get; internal set; }
    public List<SceneObject> Children { get; } = new();   // mutated only through Scene

    // Which precision positions keep, for every object (File > Project Settings). The editor and the player set it before a
    // scene loads. With Float, a position is rounded to a float whenever it is set, so nothing changes from before.
    public static PositionType PositionPrecision { get; set; } = PositionType.Float;

    // The position (local to the parent), held as doubles. Position is the same value as floats, for the many places that
    // work in floats; setting Position replaces all the digits with that float, while PositionD keeps them.
    private Vector3D _position;

    public Vector3D PositionD
    {
        get => _position;
        set => _position = PositionPrecision == PositionType.Double ? value : value.ToFloatPrecision();
    }

    public Vector3 Position
    {
        get => (Vector3)_position;
        set => _position = value;
    }

    // The position in the world, worked out in doubles all the way up the parents (rotation and scale included).
    public Vector3D WorldPositionD
    {
        get
        {
            var p = _position;
            for (var a = Parent; a is not null; a = a.Parent)
                p = Vector3D.Rotate(p * new Vector3D(a.Scale), a.Rotation) + a._position;
            return p;
        }
    }

    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Scale { get; set; } = Vector3.One;

    public List<Component> Components { get; } = new();

    // Which loaded scene this object came from (see SceneManager). Empty for objects made while playing, which belong to
    // the active scene.
    public string SceneName { get; set; } = "";

    // True after SceneManager.DontDestroyOnLoad: the object stays when another scene replaces the current one.
    public bool Persistent { get; internal set; }

    public SceneObject(Guid? id = null) => Id = id ?? Guid.NewGuid();

    // This object's position, rotation and scale, as in sceneObject.transform.position. It reads and writes the
    // object itself, so there is nothing to keep in step.
    public Transform transform => _transform ??= new Transform(this);
    private Transform? _transform;

    public Matrix4x4 LocalMatrix =>
        Matrix4x4.CreateScale(Scale) *
        Matrix4x4.CreateFromQuaternion(Rotation) *
        Matrix4x4.CreateTranslation(Position);

    public Matrix4x4 WorldMatrix => Parent is null ? LocalMatrix : LocalMatrix * Parent.WorldMatrix;
}

// Rotates the object around the Y axis while the scene is playing.
public sealed class Spin : Component
{
    public float Speed { get; set; } = 1f;   // radians per second
}

// Sun-style light. Direction comes from yaw and pitch in degrees; it points from the scene toward the light.
public sealed class DirectionalLight : Component
{
    public float Intensity { get; set; } = 2f;
    public Vector3 Color { get; set; } = Vector3.One;
    public float Yaw { get; set; } = 33.69f;
    public float Pitch { get; set; } = 47.96f;

    // Shadows from this light (only the sun, the scene's first directional light, casts them). Strength 1 means the sun
    // is fully blocked in the shade; less lets some through. ShadowDistance is how far from the camera shadows reach,
    // in world units: shorter gives sharper shadows, longer covers more of a big scene.
    public bool Shadows { get; set; } = true;
    public float ShadowStrength { get; set; } = 1f;
    public float ShadowDistance { get; set; } = 40f;

    public Vector3 Direction
    {
        get
        {
            float y = Yaw * MathF.PI / 180f;
            float p = Pitch * MathF.PI / 180f;
            return Vector3.Normalize(new Vector3(MathF.Cos(p) * MathF.Sin(y), MathF.Sin(p), MathF.Cos(p) * MathF.Cos(y)));
        }
    }

    public Vector3 Radiance => Color * Intensity;
}

// A light that shines in every direction from the object's position. Its brightness fades smoothly to nothing at Range
// (in world units), so a light only affects what is near it. Intensity multiplies Color, as with DirectionalLight.
public sealed class PointLight : Component
{
    public Vector3 Color { get; set; } = Vector3.One;
    public float Intensity { get; set; } = 3f;
    public float Range { get; set; } = 10f;

    public Vector3 Radiance => Color * Intensity;
}

// A cone of light from the object's position along its local -Z axis (like a camera): aim it by rotating the object.
// Full brightness inside InnerAngle, fading to nothing at OuterAngle (both are the half-angle of the cone, in degrees).
public sealed class SpotLight : Component
{
    public Vector3 Color { get; set; } = Vector3.One;
    public float Intensity { get; set; } = 6f;
    public float Range { get; set; } = 15f;
    public float InnerAngle { get; set; } = 20f;
    public float OuterAngle { get; set; } = 35f;

    public Vector3 Radiance => Color * Intensity;
}

// What is drawn behind the scene.
public enum SkyMode { Color, Gradient, Panorama }

// The scene's sky and ambient light. Only the first one in the scene is used; a scene without one looks as scenes
// always have: a dark blue backdrop and a little flat ambient light.
public sealed class SceneEnvironment : Component
{
    public SkyMode Sky { get; set; } = SkyMode.Gradient;

    // Color mode uses SkyColor alone. Gradient blends from GroundColor below the horizon, through HorizonColor, up
    // to SkyColor overhead. Panorama draws an equirectangular image (PNG, JPEG or Radiance .hdr) from Panorama,
    // a path relative to the project folder, and falls back to SkyColor until it loads.
    public Vector3 SkyColor { get; set; } = new(0.25f, 0.45f, 0.8f);
    public Vector3 HorizonColor { get; set; } = new(0.75f, 0.82f, 0.9f);
    public Vector3 GroundColor { get; set; } = new(0.25f, 0.25f, 0.28f);
    public string Panorama { get; set; } = "";
    public float SkyIntensity { get; set; } = 1f;

    // Light that reaches every surface equally, regardless of the lights. Color times Intensity, times the surface color.
    public Vector3 AmbientColor { get; set; } = Vector3.One;
    public float AmbientIntensity { get; set; } = 0.08f;

    // How much the sky lights the scene: soft light from above (and the horizon and ground) on surfaces,
    // and reflections of the sky on shiny ones. 0 turns it off.
    public float SkyLighting { get; set; } = 0.5f;
}

// Viewpoint for play mode. The camera looks down its local -Z axis, like the Editor's fly camera at yaw 0.
// FocalLength is in millimeters on a 36 mm wide sensor, so 35 mm gives a horizontal view of about 54 degrees.
// How a camera smooths jagged edges. Msaa works while the scene is drawn (sharpest, costs the most memory); Fxaa and Smaa work
// on the finished picture, so they also smooth edges inside textures and shaders. Smaa is cleaner than Fxaa and a little slower.
public enum AntiAliasingMode { None, Msaa, Fxaa, Smaa }

public sealed class CameraComponent : Component
{
    public float FocalLength { get; set; } = 35f;
    public float Near { get; set; } = 0.1f;
    public float Far { get; set; } = 500f;
    public AntiAliasingMode AntiAliasing { get; set; } = AntiAliasingMode.None;

    // Samples per pixel when AntiAliasing is Msaa: 2, 4 or 8. A GPU that can't do that many uses its highest.
    public int MsaaSamples { get; set; } = 4;

    // The nearest allowed MSAA sample count (2, 4 or 8) to any number.
    public static int NormalizeMsaaSamples(int samples) => samples <= 2 ? 2 : samples <= 4 ? 4 : 8;

    // Samples per pixel this camera asks for: 1 (off), 2, 4 or 8. Only MSAA uses samples; FXAA and SMAA draw with one.
    public int RequestedSamples => AntiAliasing == AntiAliasingMode.Msaa ? NormalizeMsaaSamples(MsaaSamples) : 1;

    // Vertical field of view in radians for an aspect ratio (width / height).
    public float VerticalFov(float aspect)
    {
        float focal = MathF.Max(FocalLength, 1f);
        float horizontal = 2f * MathF.Atan(18f / focal);
        return 2f * MathF.Atan(MathF.Tan(horizontal / 2f) / aspect);
    }

    // View matrix for a camera object whose world transform is `world`.
    public Matrix4x4 View(Matrix4x4 world)
    {
        var eye = world.Translation;
        var forward = Vector3.TransformNormal(-Vector3.UnitZ, world);
        var up = Vector3.TransformNormal(Vector3.UnitY, world);
        return Matrix4x4.CreateLookAt(eye, eye + forward, up);
    }
}
