using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Engine.Core.Scene;
using Engine.Core.Scripting;

namespace Engine.Core.Serialization;

public sealed record GameManifest(string Name, string StartScene);

public static class SceneSerializer
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new Vector3Json(), new Vector4Json(), new JsonStringEnumConverter() },
    };

    // A particle system's settings as JSON, in the same form scenes store them. Two systems with the same text have
    // the same settings, which is how a system is matched to a preset.
    public static string ParticleJson(ParticleSystem particles) => JsonSerializer.Serialize(particles, Options);

    private sealed class SceneFile
    {
        public int FormatVersion { get; set; } = SceneSerializer.FormatVersion;
        public string Name { get; set; } = "";
        public SettingsRecord? Settings { get; set; }
        public List<ObjectRecord> Objects { get; set; } = new();
    }

    // A scene's own settings. Scenes saved before these existed have none, and get the defaults.
    private sealed class SettingsRecord
    {
        public bool? Shadows { get; set; }
        public int? ShadowResolution { get; set; }
        public float[]? Gravity { get; set; }
    }

    private sealed class ObjectRecord
    {
        public Guid Id { get; set; }
        public Guid? Parent { get; set; }
        public string Name { get; set; } = "";
        public float[] Position { get; set; } = [0, 0, 0];
        public float[] Rotation { get; set; } = [0, 0, 0, 1];
        public float[] Scale { get; set; } = [1, 1, 1];
        public string? SceneName { get; set; }      // which loaded scene the object came from; written only in save games
        public bool? Persistent { get; set; }       // marked DontDestroyOnLoad; written only in save games
        public List<ComponentRecord> Components { get; set; } = new();
    }

    // One paint layer of a terrain: its name, color and where it is painted (compressed, base64; empty when nowhere).
    private sealed class TerrainLayerRecord
    {
        public string Name { get; set; } = "";
        public float[]? Color { get; set; }
        public string? Weights { get; set; }
    }

    // A terrain's settings and its heights (compressed, base64).
    private sealed class TerrainRecord
    {
        public List<TerrainLayerRecord>? Layers { get; set; }
        public float Width { get; set; } = 60f;
        public float Length { get; set; } = 60f;
        public float Height { get; set; } = 12f;
        public int Resolution { get; set; } = 129;
        public string? Heights { get; set; }
        public float[]? BaseColor { get; set; }
        public float[]? MidColor { get; set; }   // read only: terrains saved with automatic colors, whose grass color becomes the base
        public bool Collide { get; set; } = true;
        public float Friction { get; set; } = 0.5f;
        public float Bounciness { get; set; }
    }

    private sealed class ComponentRecord
    {
        public TerrainRecord? TerrainData { get; set; }

        public string Type { get; set; } = "";
        public string? Model { get; set; }
        public float[]? Tint { get; set; }
        public float? Speed { get; set; }
        public float? Intensity { get; set; }
        public float[]? LightColor { get; set; }
        public float? Yaw { get; set; }
        public float? Pitch { get; set; }
        public float? Range { get; set; }
        public float? InnerAngle { get; set; }
        public float? OuterAngle { get; set; }
        public bool? Shadows { get; set; }
        public float? ShadowStrength { get; set; }
        public float? ShadowDistance { get; set; }
        public string? SkyMode { get; set; }
        public float[]? SkyColor { get; set; }
        public float[]? HorizonColor { get; set; }
        public float[]? GroundColor { get; set; }
        public string? Panorama { get; set; }
        public float? SkyIntensity { get; set; }
        public float[]? AmbientColor { get; set; }
        public float? AmbientIntensity { get; set; }
        public float? SkyLighting { get; set; }
        public float? FocalLength { get; set; }
        public float? Near { get; set; }
        public float? Far { get; set; }
        public string? AntiAliasing { get; set; }
        public int? MsaaSamples { get; set; }
        public string? ScriptName { get; set; }
        public string? UiKind { get; set; }
        public string? Label { get; set; }
        public float[]? Rect { get; set; }
        public string? Text { get; set; }
        public bool? Checked { get; set; }
        public string[]? Options { get; set; }
        public int? Selected { get; set; }
        public float? Value { get; set; }
        public bool? CenterText { get; set; }
        public bool? Visible { get; set; }        // UI: written only when false, so older scenes read as visible
        public bool? Interactable { get; set; }   // UI: the same
        public float? Mass { get; set; }
        public bool? UseGravity { get; set; }
        public bool? IsKinematic { get; set; }
        public bool? FreezeRotation { get; set; }
        public float? Drag { get; set; }
        public float? AngularDrag { get; set; }
        public float[]? Center { get; set; }
        public float[]? Size { get; set; }
        public float? Radius { get; set; }
        public float? Friction { get; set; }
        public float? Bounciness { get; set; }
        public Dictionary<string, string>? Fields { get; set; }
        public string? Clip { get; set; }
        public bool? Mute { get; set; }
        public bool? PlayOnAwake { get; set; }
        public bool? Loop { get; set; }
        public int? Priority { get; set; }
        public float? Volume { get; set; }
        public float? StereoPan { get; set; }
        public float? SpatialBlend { get; set; }
        public float? DopplerLevel { get; set; }
        public float? Spread { get; set; }
        public string? Rolloff { get; set; }
        public float? MinDistance { get; set; }
        public float? MaxDistance { get; set; }

        // State of a game in progress. Written only in save games.
        public bool? Started { get; set; }              // a script whose Start already ran
        public float[]? Velocity { get; set; }          // a rigidbody's speed
        public float[]? AngularVelocity { get; set; }
        public bool? Playing { get; set; }              // an audio source that was playing
        public float? PlayTime { get; set; }            // and where it was in its clip, in seconds

        public ParticleSystem? Particles { get; set; }   // every particle module, as nested objects

        public List<AnimationEntry>? Animations { get; set; }   // an Animator's animations
        public string? AnimationId { get; set; }                // save games: the animation that was playing
        public float? AnimationTime { get; set; }               // and how far into it it was
    }

    public static void Save(Scene.Scene scene, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(scene));
    }

    // The scene as the JSON text Save writes. The Editor compares this with the last saved copy to detect unsaved edits.
    // runtimeState also writes what changes while a game plays and is not part of a scene file: each object's loaded scene
    // and persistence, rigidbody speeds, which scripts have started and their current public fields, and what the audio
    // sources are playing. Save games use it.
    public static string ToJson(Scene.Scene scene, bool runtimeState = false)
    {
        var file = new SceneFile { Name = scene.Name, Settings = ToSettingsRecord(scene.Settings, scene.Physics.Gravity) };
        foreach (var o in scene.All)                     // parents are always added before children
            file.Objects.Add(ToRecord(o, runtimeState));

        return JsonSerializer.Serialize(file, Options);
    }

    public static Scene.Scene Load(string path) => FromJson(File.ReadAllText(path), path);

    // Builds a scene from JSON text in the format ToJson writes. source names the text in error messages.
    public static Scene.Scene FromJson(string json, string source = "scene")
    {
        var file = JsonSerializer.Deserialize<SceneFile>(json, Options)
                   ?? throw new InvalidDataException($"Empty scene: {source}");
        if (file.FormatVersion > FormatVersion)
            throw new NotSupportedException($"Scene format {file.FormatVersion} is newer than this engine supports.");

        var scene = new Scene.Scene { Name = file.Name };
        ApplySettings(file.Settings, scene.Settings, out var gravity);
        scene.Physics.Gravity = gravity;
        var byId = new Dictionary<Guid, SceneObject>();

        foreach (var rec in file.Objects)
        {
            var o = FromRecord(rec, rec.Id);

            SceneObject? parent = null;
            if (rec.Parent is { } pid)
                parent = byId.TryGetValue(pid, out var p)
                    ? p
                    : throw new InvalidDataException($"'{rec.Name}' references a parent that is not defined before it.");

            scene.Add(o, parent);
            byId[o.Id] = o;
        }
        return scene;
    }

    // ---- scene settings ----------------------------------------------------------------------

    private static readonly Vector3 DefaultGravity = new(0f, -9.81f, 0f);

    private static SettingsRecord ToSettingsRecord(SceneSettings settings, Vector3 gravity) => new()
    {
        Shadows = settings.Shadows,
        ShadowResolution = settings.ShadowResolution,
        Gravity = [gravity.X, gravity.Y, gravity.Z],
    };

    private static void ApplySettings(SettingsRecord? record, SceneSettings settings, out Vector3 gravity)
    {
        settings.Shadows = record?.Shadows ?? true;
        settings.ShadowResolution = record?.ShadowResolution ?? SceneSettings.DefaultShadowResolution;
        gravity = Vec3(record?.Gravity, DefaultGravity);
    }

    // A scene file's settings and gravity, read without building its objects. For the editor's scene settings view.
    public static (SceneSettings Settings, Vector3 Gravity) ReadSettings(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException($"Empty scene: {path}");
        var record = node["settings"]?.Deserialize<SettingsRecord>(Options);
        var settings = new SceneSettings();
        ApplySettings(record, settings, out var gravity);
        return (settings, gravity);
    }

    // Writes new settings into a scene file and leaves everything else in it as it was.
    public static void WriteSettings(string path, SceneSettings settings, Vector3 gravity)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException($"Empty scene: {path}");
        node["settings"] = JsonSerializer.SerializeToNode(ToSettingsRecord(settings, gravity), Options);
        File.WriteAllText(path, node.ToJsonString(Options));
    }

    // Copies source and everything under it into the scene, next to source (same parent), with new ids.
    // The copy is named like "Crate (1)", using the first number no sibling has. Returns the copy's root.
    public static SceneObject Duplicate(Scene.Scene scene, SceneObject source)
    {
        var originals = Scene.Scene.Subtree(source).ToList();   // parents come before their children
        var copies = new Dictionary<SceneObject, SceneObject>();
        foreach (var original in originals)
        {
            var copy = FromRecord(ToRecord(original), Guid.NewGuid());
            copies[original] = copy;
            scene.Add(copy, original == source ? source.Parent : copies[original.Parent!]);
        }

        var root = copies[source];
        root.Name = CopyName(scene, source);
        return root;
    }

    private static string CopyName(Scene.Scene scene, SceneObject source)
    {
        var baseName = System.Text.RegularExpressions.Regex.Replace(source.Name, @" \(\d+\)$", "");
        var siblings = scene.All.Where(o => o.Parent == source.Parent).Select(o => o.Name).ToHashSet();
        for (int n = 1; ; n++)
            if (!siblings.Contains($"{baseName} ({n})"))
                return $"{baseName} ({n})";
    }

    // One object as its record: transform, parent link, and every component that is saved.
    private static ObjectRecord ToRecord(SceneObject o, bool runtime = false)
    {
        var rec = new ObjectRecord
        {
            Id = o.Id,
            Parent = o.Parent?.Id,
            Name = o.Name,
            Position = [o.Position.X, o.Position.Y, o.Position.Z],
            Rotation = [o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W],
            Scale = [o.Scale.X, o.Scale.Y, o.Scale.Z],
            SceneName = runtime && o.SceneName.Length > 0 ? o.SceneName : null,
            Persistent = runtime && o.Persistent ? true : null,
        };
        foreach (var c in o.Components)
        {
            switch (c)
            {
                case MeshRenderer mr:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "MeshRenderer",
                        Model = mr.Model,
                        Tint = [mr.Tint.X, mr.Tint.Y, mr.Tint.Z, mr.Tint.W],
                    });
                    break;
                case DirectionalLight light:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "DirectionalLight",
                        Intensity = light.Intensity,
                        LightColor = [light.Color.X, light.Color.Y, light.Color.Z],
                        Yaw = light.Yaw,
                        Pitch = light.Pitch,
                        Shadows = light.Shadows ? null : false,
                        ShadowStrength = light.ShadowStrength == 1f ? null : light.ShadowStrength,
                        ShadowDistance = light.ShadowDistance == 40f ? null : light.ShadowDistance,
                    });
                    break;
                case ScriptComponent script:
                {
                    var fields = new Dictionary<string, string>(script.Fields);
                    object? live = script.Instance ?? (object?)script.ComponentInstance;
                    if (runtime && live is not null)
                        foreach (var (name, json) in ScriptFields.Capture(live))
                            fields[name] = json;
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "Script",
                        ScriptName = script.ScriptName,
                        Fields = fields.Count > 0 ? fields : null,
                        Started = runtime && script.Started ? true : null,
                    });
                    break;
                }
                case AudioSource audio:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "AudioSource",
                        Clip = audio.Clip,
                        Mute = audio.Mute,
                        PlayOnAwake = audio.PlayOnAwake,
                        Loop = audio.Loop,
                        Priority = audio.Priority,
                        Volume = audio.Volume,
                        Pitch = audio.Pitch,
                        StereoPan = audio.StereoPan,
                        SpatialBlend = audio.SpatialBlend,
                        DopplerLevel = audio.DopplerLevel,
                        Spread = audio.Spread,
                        Rolloff = audio.RolloffMode.ToString(),
                        MinDistance = audio.MinDistance,
                        MaxDistance = audio.MaxDistance,
                        Playing = runtime ? audio.IsPlaying : null,
                        PlayTime = runtime && audio.IsPlaying ? audio.Time : null,
                    });
                    break;
                case AudioListener:
                    rec.Components.Add(new ComponentRecord { Type = "AudioListener" });
                    break;
                case PointLight point:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "PointLight",
                        LightColor = [point.Color.X, point.Color.Y, point.Color.Z],
                        Intensity = point.Intensity,
                        Range = point.Range,
                    });
                    break;
                case SpotLight spot:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "SpotLight",
                        LightColor = [spot.Color.X, spot.Color.Y, spot.Color.Z],
                        Intensity = spot.Intensity,
                        Range = spot.Range,
                        InnerAngle = spot.InnerAngle,
                        OuterAngle = spot.OuterAngle,
                    });
                    break;
                case SceneEnvironment env:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "Environment",
                        SkyMode = env.Sky.ToString(),
                        SkyColor = [env.SkyColor.X, env.SkyColor.Y, env.SkyColor.Z],
                        HorizonColor = [env.HorizonColor.X, env.HorizonColor.Y, env.HorizonColor.Z],
                        GroundColor = [env.GroundColor.X, env.GroundColor.Y, env.GroundColor.Z],
                        Panorama = env.Panorama.Length > 0 ? env.Panorama : null,
                        SkyIntensity = env.SkyIntensity,
                        AmbientColor = [env.AmbientColor.X, env.AmbientColor.Y, env.AmbientColor.Z],
                        AmbientIntensity = env.AmbientIntensity,
                        SkyLighting = env.SkyLighting,
                    });
                    break;
                case CameraComponent cam:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "Camera",
                        FocalLength = cam.FocalLength,
                        Near = cam.Near,
                        Far = cam.Far,
                        // Written only when on, so scenes without anti-aliasing stay as they were.
                        AntiAliasing = cam.AntiAliasing == AntiAliasingMode.None ? null : cam.AntiAliasing.ToString(),
                        MsaaSamples = cam.AntiAliasing == AntiAliasingMode.None ? null : CameraComponent.NormalizeMsaaSamples(cam.MsaaSamples),
                    });
                    break;
                case UiElement ui:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "Ui",
                        UiKind = ui.Kind.ToString(),
                        Label = ui.Label,
                        Rect = [ui.X, ui.Y, ui.W, ui.H],
                        Text = ui.Text,
                        Checked = ui.Checked,
                        Options = ui.Options.ToArray(),
                        Selected = ui.Selected,
                        Value = ui.Value,
                        CenterText = ui.CenterText,
                        Visible = ui.Visible ? null : false,
                        Interactable = ui.Interactable ? null : false,
                    });
                    break;
                case Spin spin:
                    rec.Components.Add(new ComponentRecord { Type = "Spin", Speed = spin.Speed });
                    break;
                case Rigidbody rb:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "Rigidbody",
                        Mass = rb.Mass,
                        UseGravity = rb.UseGravity,
                        IsKinematic = rb.IsKinematic,
                        FreezeRotation = rb.FreezeRotation,
                        Drag = rb.Drag,
                        AngularDrag = rb.AngularDrag,
                        Velocity = runtime ? [rb.Velocity.X, rb.Velocity.Y, rb.Velocity.Z] : null,
                        AngularVelocity = runtime ? [rb.AngularVelocity.X, rb.AngularVelocity.Y, rb.AngularVelocity.Z] : null,
                    });
                    break;
                case BoxCollider box:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "BoxCollider",
                        Center = [box.Center.X, box.Center.Y, box.Center.Z],
                        Size = [box.Size.X, box.Size.Y, box.Size.Z],
                        Friction = box.Friction,
                        Bounciness = box.Bounciness,
                    });
                    break;
                case Terrain terrain:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "Terrain",
                        TerrainData = new TerrainRecord
                        {
                            Width = terrain.Width, Length = terrain.Length, Height = terrain.Height,
                            Resolution = terrain.Resolution, Heights = terrain.HeightsData,
                            BaseColor = [terrain.BaseColor.X, terrain.BaseColor.Y, terrain.BaseColor.Z],
                            Collide = terrain.Collide, Friction = terrain.Friction, Bounciness = terrain.Bounciness,
                            Layers = terrain.Layers.Select((layer, i) => new TerrainLayerRecord
                            {
                                Name = layer.Name,
                                Color = [layer.Color.X, layer.Color.Y, layer.Color.Z],
                                Weights = terrain.LayerWeightsData(i),
                            }).ToList(),
                        },
                    });
                    break;
                case Animator animator:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "Animator",
                        Speed = animator.Speed,
                        Animations = animator.Animations.Select(a => new AnimationEntry
                        {
                            Id = a.Id, Model = a.Model, Clip = a.Clip, PlayAtStart = a.PlayAtStart, Loop = a.Loop, Speed = a.Speed,
                        }).ToList(),
                        Started = runtime && animator.Started ? true : null,
                        AnimationId = runtime && animator.IsPlaying() ? animator.CurrentId : null,
                        AnimationTime = runtime && animator.IsPlaying() ? animator.Time : null,
                    });
                    break;
                case ParticleSystem particles:
                    rec.Components.Add(new ComponentRecord { Type = "ParticleSystem", Particles = particles });
                    break;
                case MeshCollider meshCollider:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "MeshCollider",
                        Model = meshCollider.Model.Length > 0 ? meshCollider.Model : null,
                        Center = [meshCollider.Center.X, meshCollider.Center.Y, meshCollider.Center.Z],
                        Friction = meshCollider.Friction,
                        Bounciness = meshCollider.Bounciness,
                    });
                    break;
                case SphereCollider sphere:
                    rec.Components.Add(new ComponentRecord
                    {
                        Type = "SphereCollider",
                        Center = [sphere.Center.X, sphere.Center.Y, sphere.Center.Z],
                        Radius = sphere.Radius,
                        Friction = sphere.Friction,
                        Bounciness = sphere.Bounciness,
                    });
                    break;
            }
        }
        return rec;
    }

    // An object from its record, with the given id and no parent. Components are created fresh.
    private static SceneObject FromRecord(ObjectRecord rec, Guid id)
    {
        var o = new SceneObject(id)
        {
            Name = rec.Name,
            Position = new(rec.Position[0], rec.Position[1], rec.Position[2]),
            Rotation = new(rec.Rotation[0], rec.Rotation[1], rec.Rotation[2], rec.Rotation[3]),
            Scale = new(rec.Scale[0], rec.Scale[1], rec.Scale[2]),
            SceneName = rec.SceneName ?? "",
            Persistent = rec.Persistent ?? false,
        };
        foreach (var c in rec.Components)
        {
            switch (c.Type)
            {
                case "MeshRenderer":
                    o.Components.Add(new MeshRenderer
                    {
                        Model = c.Model ?? "",
                        Tint = c.Tint is { Length: 4 } t ? new Vector4(t[0], t[1], t[2], t[3]) : Vector4.One,
                    });
                    break;
                case "DirectionalLight":
                    o.Components.Add(new DirectionalLight
                    {
                        Intensity = c.Intensity ?? 2f,
                        Color = c.LightColor is { Length: 3 } lc ? new Vector3(lc[0], lc[1], lc[2]) : Vector3.One,
                        Yaw = c.Yaw ?? 33.69f,
                        Pitch = c.Pitch ?? 47.96f,
                        Shadows = c.Shadows ?? true,
                        ShadowStrength = c.ShadowStrength ?? 1f,
                        ShadowDistance = c.ShadowDistance ?? 40f,
                    });
                    break;
                case "Script":
                    o.Components.Add(new ScriptComponent
                    {
                        ScriptName = c.ScriptName ?? "",
                        Fields = c.Fields is { } fields ? new Dictionary<string, string>(fields) : new(),
                        RestoredStarted = c.Started ?? false,
                    });
                    break;
                case "AudioSource":
                {
                    var defaults = new AudioSource();
                    o.Components.Add(new AudioSource
                    {
                        Clip = c.Clip ?? "",
                        Mute = c.Mute ?? defaults.Mute,
                        PlayOnAwake = c.PlayOnAwake ?? defaults.PlayOnAwake,
                        Loop = c.Loop ?? defaults.Loop,
                        Priority = c.Priority ?? defaults.Priority,
                        Volume = c.Volume ?? defaults.Volume,
                        Pitch = c.Pitch ?? defaults.Pitch,
                        StereoPan = c.StereoPan ?? defaults.StereoPan,
                        SpatialBlend = c.SpatialBlend ?? defaults.SpatialBlend,
                        DopplerLevel = c.DopplerLevel ?? defaults.DopplerLevel,
                        Spread = c.Spread ?? defaults.Spread,
                        RolloffMode = Enum.TryParse<AudioRolloffMode>(c.Rolloff, out var rolloff) ? rolloff : defaults.RolloffMode,
                        MinDistance = c.MinDistance ?? defaults.MinDistance,
                        MaxDistance = c.MaxDistance ?? defaults.MaxDistance,
                        // A source saved mid-game has already woken up; it carries on from where it was, or stays quiet.
                        Awoke = c.Playing is not null,
                        ResumeAt = c.Playing == true ? c.PlayTime ?? 0f : -1f,
                    });
                    break;
                }
                case "AudioListener":
                    o.Components.Add(new AudioListener());
                    break;
                case "PointLight":
                {
                    var defaults = new PointLight();
                    o.Components.Add(new PointLight
                    {
                        Color = Vec3(c.LightColor, defaults.Color),
                        Intensity = c.Intensity ?? defaults.Intensity,
                        Range = c.Range ?? defaults.Range,
                    });
                    break;
                }
                case "SpotLight":
                {
                    var defaults = new SpotLight();
                    o.Components.Add(new SpotLight
                    {
                        Color = Vec3(c.LightColor, defaults.Color),
                        Intensity = c.Intensity ?? defaults.Intensity,
                        Range = c.Range ?? defaults.Range,
                        InnerAngle = c.InnerAngle ?? defaults.InnerAngle,
                        OuterAngle = c.OuterAngle ?? defaults.OuterAngle,
                    });
                    break;
                }
                case "Environment":
                {
                    var defaults = new SceneEnvironment();
                    o.Components.Add(new SceneEnvironment
                    {
                        Sky = Enum.TryParse<SkyMode>(c.SkyMode, out var sky) ? sky : defaults.Sky,
                        SkyColor = Vec3(c.SkyColor, defaults.SkyColor),
                        HorizonColor = Vec3(c.HorizonColor, defaults.HorizonColor),
                        GroundColor = Vec3(c.GroundColor, defaults.GroundColor),
                        Panorama = c.Panorama ?? "",
                        SkyIntensity = c.SkyIntensity ?? defaults.SkyIntensity,
                        AmbientColor = Vec3(c.AmbientColor, defaults.AmbientColor),
                        AmbientIntensity = c.AmbientIntensity ?? defaults.AmbientIntensity,
                        SkyLighting = c.SkyLighting ?? defaults.SkyLighting,
                    });
                    break;
                }
                case "Camera":
                    o.Components.Add(new CameraComponent
                    {
                        FocalLength = c.FocalLength ?? 35f,
                        Near = c.Near ?? 0.1f,
                        Far = c.Far ?? 500f,
                        AntiAliasing = Enum.TryParse<AntiAliasingMode>(c.AntiAliasing, out var aa) ? aa : AntiAliasingMode.None,
                        MsaaSamples = CameraComponent.NormalizeMsaaSamples(c.MsaaSamples ?? 4),
                    });
                    break;
                case "Ui":
                {
                    var ui = new UiElement
                    {
                        Kind = Enum.TryParse<UiKind>(c.UiKind, out var kind) ? kind : UiKind.Button,
                        Label = c.Label ?? "Button",
                        Text = c.Text ?? "",
                        Checked = c.Checked ?? false,
                        Options = c.Options?.ToList() ?? new List<string>(),
                        Selected = c.Selected ?? 0,
                        Value = c.Value ?? 0.5f,
                        CenterText = c.CenterText ?? true,
                        Visible = c.Visible ?? true,
                        Interactable = c.Interactable ?? true,
                    };
                    if (c.Rect is { Length: 4 } rect)
                    {
                        ui.X = rect[0];
                        ui.Y = rect[1];
                        ui.W = rect[2];
                        ui.H = rect[3];
                    }
                    o.Components.Add(ui);
                    break;
                }
                case "Spin":
                    o.Components.Add(new Spin { Speed = c.Speed ?? 1f });
                    break;
                case "Rigidbody":
                    o.Components.Add(new Rigidbody
                    {
                        Mass = c.Mass ?? 1f,
                        UseGravity = c.UseGravity ?? true,
                        IsKinematic = c.IsKinematic ?? false,
                        FreezeRotation = c.FreezeRotation ?? false,
                        Drag = c.Drag ?? 0.05f,
                        AngularDrag = c.AngularDrag ?? 0.05f,
                        Velocity = Vec3(c.Velocity, Vector3.Zero),
                        AngularVelocity = Vec3(c.AngularVelocity, Vector3.Zero),
                    });
                    break;
                case "BoxCollider":
                    o.Components.Add(new BoxCollider
                    {
                        Center = Vec3(c.Center, Vector3.Zero),
                        Size = Vec3(c.Size, Vector3.One),
                        Friction = c.Friction ?? 0.5f,
                        Bounciness = c.Bounciness ?? 0f,
                    });
                    break;
                case "Terrain":
                {
                    var data = c.TerrainData ?? new TerrainRecord();
                    var defaults = new Terrain();
                    var terrain = new Terrain
                    {
                        Width = data.Width, Length = data.Length, Height = data.Height,
                        BaseColor = Vec3(data.BaseColor ?? data.MidColor, defaults.BaseColor),
                        Collide = data.Collide, Friction = data.Friction, Bounciness = data.Bounciness,
                    };
                    terrain.LoadHeights(data.Resolution, data.Heights);
                    foreach (var layer in data.Layers ?? new())
                        terrain.LoadLayer(layer.Name ?? "Layer", Vec3(layer.Color, Vector3.One), layer.Weights);
                    o.Components.Add(terrain);
                    break;
                }
                case "Animator":
                {
                    var animator = new Animator { Speed = c.Speed ?? 1f, Started = c.Started ?? false };
                    foreach (var a in c.Animations ?? new())
                        animator.Animations.Add(new AnimationEntry
                        {
                            Id = a.Id ?? "", Model = a.Model ?? "", Clip = a.Clip ?? "", PlayAtStart = a.PlayAtStart, Loop = a.Loop, Speed = a.Speed,
                        });
                    // A game saved mid-animation carries on from where it was.
                    if (c.AnimationId is { Length: > 0 } playing)
                    {
                        animator.ResumeId = playing;
                        animator.ResumeTime = c.AnimationTime ?? 0f;
                        animator.Started = false;
                    }
                    o.Components.Add(animator);
                    break;
                }
                case "ParticleSystem":
                    // Copied through JSON, so a duplicated object never shares settings with the original.
                    o.Components.Add(c.Particles is { } settings
                        ? JsonSerializer.Deserialize<ParticleSystem>(JsonSerializer.Serialize(settings, Options), Options)!
                        : new ParticleSystem());
                    break;
                case "MeshCollider":
                    o.Components.Add(new MeshCollider
                    {
                        Model = c.Model ?? "",
                        Center = Vec3(c.Center, Vector3.Zero),
                        Friction = c.Friction ?? 0.5f,
                        Bounciness = c.Bounciness ?? 0f,
                    });
                    break;
                case "SphereCollider":
                    o.Components.Add(new SphereCollider
                    {
                        Center = Vec3(c.Center, Vector3.Zero),
                        Radius = c.Radius ?? 0.5f,
                        Friction = c.Friction ?? 0.5f,
                        Bounciness = c.Bounciness ?? 0f,
                    });
                    break;
                default:
                    throw new NotSupportedException($"Unknown component type '{c.Type}'.");
            }
        }
        return o;
    }

    private static Vector3 Vec3(float[]? values, Vector3 fallback) =>
        values is { Length: 3 } v ? new Vector3(v[0], v[1], v[2]) : fallback;
}

// Vectors as [x, y, z] and colors as [r, g, b, a], matching how positions and tints are written.
internal sealed class Vector3Json : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var v = JsonSerializer.Deserialize<float[]>(ref reader) ?? [];
        return v.Length == 3 ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new[] { value.X, value.Y, value.Z });
}

internal sealed class Vector4Json : JsonConverter<Vector4>
{
    public override Vector4 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var v = JsonSerializer.Deserialize<float[]>(ref reader) ?? [];
        return v.Length == 4 ? new Vector4(v[0], v[1], v[2], v[3]) : Vector4.One;
    }

    public override void Write(Utf8JsonWriter writer, Vector4 value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new[] { value.X, value.Y, value.Z, value.W });
}
