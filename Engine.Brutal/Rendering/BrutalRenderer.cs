using System.Numerics;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Engine.Brutal.Gfx;
using Engine.Core.Assets;
using Engine.Core.Rendering;
using Engine.Core.Scene;

namespace Engine.Brutal.Rendering;

public sealed class BrutalRenderer : IRenderer
{
    private const int VertexStride = 48;     // 12 floats: position, normal, uv, color
    private const int PushBytes = 256;       // mvp(64) model(64) baseColor(16) material(16) camera(16) specular(16) light(16) lightColor(16) emission(16) extra(16); many GPUs cap push constants at 256
    private const int PushFloats = PushBytes / sizeof(float);
    private const int StageVertexFragment = 0x11;
    private const int OffModel = 16, OffColor = 32, OffMaterial = 36, OffCamera = 40, OffSpecular = 44, OffLight = 48, OffRadiance = 52, OffEmission = 56;
    private const int OffExtra = 60;
    private const int MaxLights = SceneLighting.MaxLights;
    private const int FrameLightVp = 24 + MaxLights * 16;     // where the sun's view-projection sits in the frame block
    private const int FrameShadow = FrameLightVp + 16;        // shadow settings, after the sun matrix
    private const int FrameFloats = FrameShadow + 4;          // ambient, sky parameters and colors, the lights, the sun matrix, shadows
    private const int MaxPanoramaWidth = 4096;                // wider sky images are shrunk, which looks the same
    private int _shadowSize = 2048;   // the shadow map's width and height; SetShadows changes it
    private int? _maxImageSize;       // the GPU's largest image side, read once
    private const float MinShadowDistance = 5f;
    private const float MaxShadowDistance = 500f;

    // The block of push constants every model shader reads, in the order they are written (see the Off* offsets).
    private const string PushBlockGlsl = """
        layout(push_constant) uniform PC {
            mat4 mvp;
            mat4 model;
            vec4 baseColor;   // object tint * material base color
            vec4 material;    // x = roughness, y = metallic, z = ior, w = alpha cutoff (0 = opaque)
            vec4 camera;      // xyz = camera position in world space
            vec4 specular;    // rgb = specular color, a = specular factor
            vec4 light;       // xyz = direction toward the sun
            vec4 lightColor;  // rgb = radiance (color * intensity)
            vec4 emission;    // rgb = emitted light (emissiveFactor * emissive strength), added unlit
            vec4 extra;      // x = occlusion map strength (0 = none), y = normal map scale (0 = no normal map)
        } pc;
        """;

    // The per-frame data shared by the model and sky shaders (descriptor set 0): ambient light, the sky, and the lights
    // other than the sun. skyEnv gives the sky's color in a direction, from the panorama or the gradient or color.
    private const string FrameGlsl = """
        const float PI = 3.14159265;
        struct Light {
            vec4 posType;     // xyz = position (point, spot) or direction toward the light (directional), w = 0, 1 or 2
            vec4 colorRange;  // rgb = radiance, w = range
            vec4 dirInner;    // xyz = the way a spot light shines, w = cos(inner angle)
            vec4 outer;       // x = cos(outer angle)
        };
        layout(std140, set = 0, binding = 1) uniform Frame {
            vec4 ambient;     // rgb = ambient color * intensity
            vec4 skyParams;   // x = sky mode (0 color, 1 gradient, 2 panorama), y = sky intensity, z = light count, w = panorama mip levels
            vec4 skyParams2;  // x = panorama is HDR, y = sky lighting strength
            vec4 zenith;
            vec4 horizon;
            vec4 ground;
            Light lights[MAX_LIGHTS];
            mat4 lightViewProj;   // sun view * projection: world space to shadow map clip space
            vec4 shadow;          // x = strength (0 = no shadows), y = one shadow texel in depth units, z = one texel in world units
        } frame;
        layout(set = 0, binding = 2) uniform sampler2D panorama;

        vec2 equirect(vec3 d) {
            return vec2(atan(d.z, d.x) * (0.5 / PI) + 0.5, acos(clamp(d.y, -1.0, 1.0)) / PI);
        }
        vec3 skyEnv(vec3 d, float lod) {
            int mode = int(frame.skyParams.x + 0.5);
            vec3 c;
            if (mode == 2) {
                c = textureLod(panorama, equirect(d), lod).rgb;
                // HDR panoramas hold linear light; the rest of the renderer works in display values.
                if (frame.skyParams2.x > 0.5) c = pow(max(c, vec3(0.0)), vec3(1.0 / 2.2));
            } else if (mode == 1) {
                c = d.y >= 0.0 ? mix(frame.horizon.rgb, frame.zenith.rgb, sqrt(d.y))
                               : mix(frame.horizon.rgb, frame.ground.rgb, sqrt(-d.y));
            } else {
                c = frame.zenith.rgb;
            }
            return c * frame.skyParams.y;
        }
        """;

    private const string VertexGlsl = """
        #version 450
        PUSH_BLOCK
        layout(location = 0) in vec3 inPos;
        layout(location = 1) in vec3 inNormal;
        layout(location = 2) in vec2 inUv;
        layout(location = 3) in vec4 inColor;
        layout(location = 0) out vec3 outWorldPos;
        layout(location = 1) out vec3 outNormal;
        layout(location = 2) out vec4 outBaseColor;
        layout(location = 3) out vec4 outMaterial;
        layout(location = 4) out vec3 outCamera;
        layout(location = 5) out vec4 outSpecular;
        layout(location = 7) out vec2 outUv;
        layout(location = 8) out vec4 outVertexColor;
        void main() {
            vec4 world = pc.model * vec4(inPos, 1.0);
            gl_Position = pc.mvp * vec4(inPos, 1.0);
            outWorldPos = world.xyz;
            outNormal = mat3(pc.model) * inNormal;
            outBaseColor = pc.baseColor;
            outMaterial = pc.material;
            outCamera = pc.camera.xyz;
            outSpecular = pc.specular;
            outUv = inUv;
            outVertexColor = inColor;
        }
        """;

    // Cook-Torrance: GGX distribution, Smith geometry, Schlick Fresnel.
    // Dielectric F0 comes from IOR; metallic blends F0 toward the base color.
    // Base color, roughness/metallic, emission and occlusion are multiplied by their maps; a normal map bends the normal.
    // Lights: the sun (push constants), the point/spot/extra directional lights in the frame block, flat ambient, and
    // light from the sky (diffuse from the blurred sky, reflections from the sky at the surface's roughness).
    private const string FragmentGlsl = """
        #version 450
        PUSH_BLOCK
        layout(location = 0) in vec3 inWorldPos;
        layout(location = 1) in vec3 inNormal;
        layout(location = 2) in vec4 inBaseColor;
        layout(location = 3) in vec4 inMaterial;
        layout(location = 4) in vec3 inCamera;
        layout(location = 5) in vec4 inSpecular;
        layout(location = 7) in vec2 inUv;
        layout(location = 8) in vec4 inVertexColor;
        layout(set = 0, binding = 0) uniform sampler2D shadowMap;
        FRAME_BLOCK
        layout(set = 1, binding = 0) uniform sampler2D baseColorMap;
        layout(set = 1, binding = 1) uniform sampler2D normalMap;
        layout(set = 1, binding = 2) uniform sampler2D metalRoughMap;
        layout(set = 1, binding = 3) uniform sampler2D emissiveMap;
        layout(set = 1, binding = 4) uniform sampler2D occlusionMap;
        layout(location = 0) out vec4 outColor;

        // 1 where the sun reaches the point, 0 where a model blocks it. The point is first moved a little off the surface
        // along its normal (more on surfaces at a grazing angle to the sun), which keeps a surface from shadowing itself in
        // speckles. Then 3x3 shadow texels are tested and blended by where the point falls between them, which gives soft,
        // smooth shadow edges on any GPU. Shadows fade out toward the edge of the shadow area instead of ending in a line.
        float shadowFactor(vec3 N, vec3 L) {
            vec3 geometric = normalize(inNormal);
            float cosT = clamp(dot(geometric, L), 0.0, 1.0);
            vec3 offsetPos = inWorldPos + geometric * frame.shadow.z * (0.6 + 1.4 * (1.0 - cosT));
            vec4 shadowPos = frame.lightViewProj * vec4(offsetPos, 1.0);
            vec3 p = shadowPos.xyz / shadowPos.w;
            vec2 uv = p.xy * 0.5 + 0.5;
            if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || p.z > 1.0 || p.z < 0.0) return 1.0;
            float tanT = min(sqrt(1.0 - cosT * cosT) / max(cosT, 0.1), 4.0);
            float bias = frame.shadow.y * (0.5 + tanT);

            ivec2 size = textureSize(shadowMap, 0);
            vec2 st = uv * vec2(size) - 0.5;
            ivec2 base = ivec2(floor(st));
            vec2 f = fract(st);
            float s[16];
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 4; i++) {
                    ivec2 c = clamp(base + ivec2(i - 1, j - 1), ivec2(0), size - 1);
                    s[j * 4 + i] = (p.z - bias > texelFetch(shadowMap, c, 0).r) ? 0.0 : 1.0;
                }
            float lit = 0.0;
            for (int j = 0; j < 3; j++)
                for (int i = 0; i < 3; i++)
                    lit += mix(mix(s[j * 4 + i], s[j * 4 + i + 1], f.x), mix(s[(j + 1) * 4 + i], s[(j + 1) * 4 + i + 1], f.x), f.y);
            lit /= 9.0;
            float edge = max(abs(p.x), abs(p.y));
            return mix(lit, 1.0, smoothstep(0.85, 1.0, edge));
        }

        // The split-sum environment BRDF, fitted by Karis (Unreal Engine 4 mobile), so no lookup texture is needed.
        vec3 envBrdf(vec3 specularColor, float roughness, float NoV) {
            const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
            const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
            vec4 r = roughness * c0 + c1;
            float a004 = min(r.x * r.x, exp2(-9.28 * NoV)) * r.x + r.y;
            vec2 AB = vec2(-1.04, 1.04) * a004 + r.zw;
            return specularColor * AB.x + AB.y;
        }

        // One light's contribution: the Cook-Torrance BRDF times N.L times the light's radiance.
        vec3 shade(vec3 N, vec3 V, vec3 L, vec3 radiance, vec3 albedo, vec3 F0, float roughness, float metallic) {
            vec3 H = normalize(V + L);
            float a = roughness * roughness;
            float a2 = a * a;
            float NdotL = max(dot(N, L), 0.0);
            float NdotV = max(dot(N, V), 1e-4);
            float NdotH = max(dot(N, H), 0.0);
            float VdotH = max(dot(V, H), 0.0);

            float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
            float D = a2 / (PI * denom * denom);
            float k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
            float G = (NdotV / (NdotV * (1.0 - k) + k)) * (NdotL / (NdotL * (1.0 - k) + k));
            vec3 F = F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);

            vec3 specular = (D * G * F) / max(4.0 * NdotV * NdotL, 1e-4);
            vec3 diffuse = (1.0 - F) * (1.0 - metallic) * albedo / PI;
            return (diffuse + specular) * NdotL * radiance;
        }

        void main() {
            // Every map is read up front, before any branch, so the screen-space derivatives below stay well defined.
            vec4 baseSample = texture(baseColorMap, inUv);
            vec4 metalRough = texture(metalRoughMap, inUv);
            vec3 emissiveSample = texture(emissiveMap, inUv).rgb;
            float occlusionSample = texture(occlusionMap, inUv).r;
            vec3 normalSample = texture(normalMap, inUv).xyz;

            vec3 N = normalize(inNormal);
            if (pc.extra.y > 0.0) {
                // Tangent frame from the screen-space derivatives of position and UV, so models need no tangent data
                // (as in the glTF sample viewer). The bitangent is cross(N, T), which keeps the map's green up.
                vec3 dpx = dFdx(inWorldPos), dpy = dFdy(inWorldPos);
                vec2 dux = dFdx(inUv), duy = dFdy(inUv);
                float det = dux.x * duy.y - duy.x * dux.y;
                if (abs(det) > 1e-14) {
                    vec3 t = (duy.y * dpx - dux.y * dpy) / det;
                    t -= N * dot(N, t);
                    float tl = length(t);
                    if (tl > 1e-9) {
                        vec3 T = t / tl;
                        vec3 B = cross(N, T);
                        vec3 tn = normalSample * 2.0 - 1.0;
                        tn.xy *= pc.extra.y;
                        N = normalize(mat3(T, B, N) * tn);
                    }
                }
            }

            vec4 baseColor = inBaseColor * baseSample * inVertexColor;
            if (inMaterial.w > 0.0 && baseColor.a < inMaterial.w) discard;

            vec3 V = normalize(inCamera - inWorldPos);
            float roughness = clamp(inMaterial.x * metalRough.g, 0.04, 1.0);
            float metallic = clamp(inMaterial.y * metalRough.b, 0.0, 1.0);
            float ior = max(inMaterial.z, 1.0);
            vec3 albedo = baseColor.rgb;

            float f0 = pow((ior - 1.0) / (ior + 1.0), 2.0);
            // KHR_materials_specular: scales dielectric reflectance only; metals keep their base color.
            vec3 dielectricF0 = min(vec3(f0) * inSpecular.rgb, vec3(1.0)) * inSpecular.a;
            vec3 F0 = mix(dielectricF0, albedo, metallic);

            // shadow.x is the shadow strength; 0 means the sun casts no shadows this frame.
            float lit = frame.shadow.x > 0.0
                ? mix(1.0, shadowFactor(N, normalize(pc.light.xyz)), frame.shadow.x)
                : 1.0;
            vec3 color = shade(N, V, normalize(pc.light.xyz), pc.lightColor.rgb, albedo, F0, roughness, metallic) * lit;

            int count = int(frame.skyParams.z + 0.5);
            for (int i = 0; i < MAX_LIGHTS; i++) {
                if (i >= count) break;
                Light l = frame.lights[i];
                int type = int(l.posType.w + 0.5);
                vec3 L;
                float attenuation = 1.0;
                if (type == 0) {
                    L = normalize(l.posType.xyz);
                } else {
                    vec3 toLight = l.posType.xyz - inWorldPos;
                    float dist = length(toLight);
                    L = toLight / max(dist, 1e-4);
                    // Fades smoothly from full brightness up close to nothing at the light's range.
                    float window = clamp(1.0 - (dist * dist) / (l.colorRange.w * l.colorRange.w), 0.0, 1.0);
                    attenuation = window * window;
                    if (type == 2) {
                        float cone = clamp((dot(-L, normalize(l.dirInner.xyz)) - l.outer.x) / max(l.dirInner.w - l.outer.x, 1e-4), 0.0, 1.0);
                        attenuation *= cone * cone;
                    }
                }
                if (attenuation > 0.0)
                    color += shade(N, V, L, l.colorRange.rgb * attenuation, albedo, F0, roughness, metallic);
            }

            float ao = mix(1.0, occlusionSample, pc.extra.x);
            vec3 ambient = frame.ambient.rgb * albedo;

            vec3 sky = vec3(0.0);
            float skyLight = frame.skyParams2.y;
            if (skyLight > 0.0) {
                float mips = frame.skyParams.w;
                float NoV = max(dot(N, V), 1e-4);
                vec3 irradiance = skyEnv(N, max(mips - 3.0, 0.0));                       // blurry: light from the whole sky
                vec3 reflected = skyEnv(reflect(-V, N), roughness * max(mips - 1.0, 0.0)); // sharper when smoother
                sky = (irradiance * albedo * (1.0 - metallic) + reflected * envBrdf(F0, roughness, NoV)) * skyLight;
            }

            color += (ambient + sky) * ao;
            // Emission is self-lit: added after lighting and not scaled by the sun or the base color.
            color += pc.emission.rgb * emissiveSample;
            outColor = vec4(color, baseColor.a);
        }
        """;

    // A full-screen triangle that draws the sky behind the models. The view direction of each pixel comes from the
    // inverse view-projection matrix, which is the first matrix of the push constants.
    private const string SkyVertexGlsl = """
        #version 450
        layout(location = 0) out vec2 outNdc;
        void main() {
            vec2 p = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
            outNdc = p * 2.0 - 1.0;
            gl_Position = vec4(outNdc, 0.0, 1.0);
        }
        """;

    private const string SkyFragmentGlsl = """
        #version 450
        layout(push_constant) uniform PC { mat4 invViewProj; } pc;
        layout(location = 0) in vec2 inNdc;
        FRAME_BLOCK
        layout(location = 0) out vec4 outColor;
        void main() {
            vec4 nearPoint = pc.invViewProj * vec4(inNdc, 0.0, 1.0);
            vec4 farPoint = pc.invViewProj * vec4(inNdc, 1.0, 1.0);
            vec3 dir = normalize(farPoint.xyz / farPoint.w - nearPoint.xyz / nearPoint.w);
            outColor = vec4(skyEnv(dir, 0.0), 1.0);
        }
        """;

    // Fills in the shared pieces of a shader's source.
    private static string Expand(string glsl) => glsl
        .Replace("PUSH_BLOCK", PushBlockGlsl)
        .Replace("FRAME_BLOCK", FrameGlsl)
        .Replace("MAX_LIGHTS", SceneLighting.MaxLights.ToString());

    private readonly VulkanContext _ctx;
    private readonly ShaderLibrary _shaders;
    private readonly Device _device;
    private readonly GpuMaterials _materials;
    private readonly GpuMeshCache _meshes;
    private readonly VkPipelineLayout _layout;
    private readonly Dictionary<int, VkPipeline> _pipelines = new();   // model pipeline per MSAA sample count
    private readonly Dictionary<int, VkPipeline> _transparentPipelines = new();   // the same, blending and not writing depth
    private readonly Dictionary<int, VkPipeline> _skyPipelines = new();   // sky pipeline per MSAA sample count
    private readonly float[] _push = new float[PushFloats];
    private Matrix4x4 _viewProjection;
    private Vector3 _eye;
    private readonly ParticleRenderer _particles;

    // Shadow map: the sun's depth, drawn before the scene pass. One copy per frame in flight, so a frame never
    // overwrites a shadow map the GPU is still reading.
    private readonly VkImage[] _shadowImages = new VkImage[VulkanContext.FramesInFlight];
    private readonly VkDeviceMemory[] _shadowMemories = new VkDeviceMemory[VulkanContext.FramesInFlight];
    private readonly VkImageView[] _shadowViews = new VkImageView[VulkanContext.FramesInFlight];
    private readonly VkDescriptorSet[] _shadowSets = new VkDescriptorSet[VulkanContext.FramesInFlight];
    private VkSampler _shadowSampler;
    private VkDescriptorSetLayout _shadowSetLayout;   // set 0: shadow map, frame data, sky panorama

    // Per-frame data in the same set: ambient light, the sky and the lights, one buffer per frame in flight.
    private readonly VkBuffer[] _frameBuffers = new VkBuffer[VulkanContext.FramesInFlight];
    private readonly VkDeviceMemory[] _frameMemories = new VkDeviceMemory[VulkanContext.FramesInFlight];
    private readonly float[] _frameData = new float[FrameFloats];
    private EnvironmentData _environment = EnvironmentData.Default;
    private IReadOnlyList<LightData> _lights = Array.Empty<LightData>();
    private VkSampler _panoramaSampler;
    private GpuTexture _blackPanorama = null!;   // stands in while the sky isn't a panorama
    private GpuTexture? _panorama;               // the sky image, when there is one
    private string _panoramaKey = "";            // which image _panorama holds ("" for none)
    private VkDescriptorPool _descriptorPool;
    private VkPipeline _shadowPipeline;
    private Matrix4x4 _lightViewProjection;
    private bool _shadowsOn = true;
    private float _shadowStrength = 1f;
    private float _shadowDistance = 40f;
    private float _shadowTexelDepth;   // one shadow texel in shadow-depth units, for the shader's bias
    private int _shadowSlot;
    private int _activeSlot;
    // Draws queued by DrawModel this frame. FlushFrame records the shadow pass from them, then draws them.
    private sealed class QueuedDraw
    {
        public required string Key { get; init; }
        public required Matrix4x4 World { get; init; }
        public required Vector4 Tint { get; init; }
        public Engine.Core.Animation.ModelPose? Pose { get; init; }
        public VkBuffer?[]? Vertices { get; set; }   // per mesh: the bent copy of the vertices for this frame, or null for the original
    }

    private readonly List<QueuedDraw> _draws = new();

    // Vertex buffers for animated (skinned) meshes, which have new vertices every frame. Each frame slot has its own
    // set, because the GPU may still be reading the other slot's while this one is written.
    private sealed class DynamicBuffer
    {
        public required VkBuffer Buffer { get; set; }
        public required VkDeviceMemory Memory { get; set; }
        public required ulong Size { get; set; }
    }
    private readonly List<DynamicBuffer>[] _dynamicBuffers = Enumerable.Range(0, VulkanContext.FramesInFlight).Select(_ => new List<DynamicBuffer>()).ToArray();
    private readonly Dictionary<int, float[]> _vertexScratch = new();
    private bool _framePending;

    public BrutalRenderer(VulkanContext ctx, ShaderLibrary shaders)
    {
        _ctx = ctx;
        _shaders = shaders;
        _device = ctx.Device;
        // Vulkan only guarantees 128 bytes of push constants. Larger blocks work where the GPU allows them and can
        // lose the device (VK_ERROR_DEVICE_LOST) where it doesn't, so say so up front.
        int pushLimit = (int)ctx.Physical.GetProperties().Limits.MaxPushConstantsSize;
        if (pushLimit < PushBytes)
            Console.WriteLine($"Warning: this GPU supports {pushLimit} bytes of push constants but the renderer uses {PushBytes}. " +
                              "If drawing fails with VK_ERROR_DEVICE_LOST, this is the likely cause.");

        _materials = new GpuMaterials(ctx);
        _meshes = new GpuMeshCache(ctx, _materials);
        CreateShadowResources(ctx.Physical);
        _shadowSetLayout = CreateShadowSetLayout();
        _layout = CreateLayout();
        _pipelines[1] = CreatePipeline(1);
        _shadowPipeline = CreateShadowPipeline();
        CreateDescriptors();
        _particles = new ParticleRenderer(ctx, shaders);
        _ctx.BeforeEndFrame = FlushFrame;
    }

    private VkPipelineLayout CreateLayout()
    {
        var range = new VkPushConstantRange
        {
            StageFlags = (VkShaderStageFlags)StageVertexFragment,
            Offset = (ByteSize32)0,
            Size = (ByteSize32)PushBytes,
        };
        return _device.CreatePipelineLayout(new[] { _shadowSetLayout, _materials.SetLayout }, new[] { range }, null);
    }

    // The model pipeline for the scene pass's sample count, made the first time that count is used.
    private VkPipeline PipelineFor(int samples)
    {
        if (!_pipelines.TryGetValue(samples, out var pipeline))
            _pipelines[samples] = pipeline = CreatePipeline(samples);
        return pipeline;
    }

    // The pipeline for see-through surfaces: blends by alpha over what is already drawn, tests depth against the solid
    // surfaces but doesn't write it, so see-through things behind each other all show.
    private VkPipeline TransparentPipelineFor(int samples)
    {
        if (!_transparentPipelines.TryGetValue(samples, out var pipeline))
            _transparentPipelines[samples] = pipeline = CreatePipeline(samples, transparent: true);
        return pipeline;
    }

    private unsafe VkPipeline CreatePipeline(int samples, bool transparent = false)
    {
        var vert = _shaders.Get("pbr.vert", Expand(VertexGlsl), ShaderKind.VertexShader);
        var frag = _shaders.Get("pbr.frag", Expand(FragmentGlsl), ShaderKind.FragmentShader);
        byte[] mainName = "main\0"u8.ToArray();

        fixed (byte* entry = mainName)
        {
            var stages = new[]
            {
                new VkPipelineShaderStageCreateInfo { Stage = (VkShaderStageFlags)0x1, Module = vert, Name = entry },
                new VkPipelineShaderStageCreateInfo { Stage = (VkShaderStageFlags)0x10, Module = frag, Name = entry },
            };
            var bindings = new[]
            {
                new VkVertexInputBindingDescription { Binding = 0, Stride = (ByteSize32)VertexStride, InputRate = (VkVertexInputRate)0 },
            };
            var attributes = new[]
            {
                new VkVertexInputAttributeDescription { Location = 0, Binding = 0, Format = (VkFormat)106, Offset = (ByteSize32)0 },
                new VkVertexInputAttributeDescription { Location = 1, Binding = 0, Format = (VkFormat)106, Offset = (ByteSize32)12 },
                new VkVertexInputAttributeDescription { Location = 2, Binding = 0, Format = (VkFormat)103, Offset = (ByteSize32)24 },
                new VkVertexInputAttributeDescription { Location = 3, Binding = 0, Format = (VkFormat)109, Offset = (ByteSize32)32 },
            };
            var colorFormats = new[] { _ctx.ColorFormat };
            var viewport = new VkViewport { Width = _ctx.Width, Height = _ctx.Height, MinDepth = 0, MaxDepth = 1 };
            var scissor = new VkRect2D { Extent = new VkExtent2D { Width = _ctx.Width, Height = _ctx.Height } };
            var blend = new VkPipelineColorBlendAttachmentState { ColorWriteMask = (VkColorComponentFlags)0xF };
            if (transparent)
            {
                blend.BlendEnable = true;
                blend.SrcColorBlendFactor = (VkBlendFactor)6;   // source alpha
                blend.DstColorBlendFactor = (VkBlendFactor)7;   // one minus source alpha
                blend.ColorBlendOp = (VkBlendOp)0;              // add
                blend.SrcAlphaBlendFactor = (VkBlendFactor)1;   // one
                blend.DstAlphaBlendFactor = (VkBlendFactor)7;
                blend.AlphaBlendOp = (VkBlendOp)0;
            }

            var dynamicStates = new[] { (VkDynamicState)0, (VkDynamicState)1 };   // viewport and scissor
            fixed (VkPipelineShaderStageCreateInfo* stagesPtr = stages)
            fixed (VkVertexInputBindingDescription* bindingsPtr = bindings)
            fixed (VkVertexInputAttributeDescription* attributesPtr = attributes)
            fixed (VkFormat* colorFormatsPtr = colorFormats)
            fixed (VkDynamicState* dynamicStatesPtr = dynamicStates)
            {
                var vertexInput = new VkPipelineVertexInputStateCreateInfo
                {
                    VertexBindingDescriptionCount = 1,
                    VertexBindingDescriptions = bindingsPtr,
                    VertexAttributeDescriptionCount = attributes.Length,
                    VertexAttributeDescriptions = attributesPtr,
                };
                var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = (VkPrimitiveTopology)3 };
                var viewportState = new VkPipelineViewportStateCreateInfo
                {
                    ViewportCount = 1, Viewports = &viewport,
                    ScissorCount = 1, Scissors = &scissor,
                };
                var raster = new VkPipelineRasterizationStateCreateInfo
                {
                    PolygonMode = (VkPolygonMode)0,
                    CullMode = (VkCullModeFlags)0,
                    FrontFace = (VkFrontFace)0,
                    LineWidth = 1f,
                };
                var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = (VkSampleCountFlags)samples };
                var depthState = new VkPipelineDepthStencilStateCreateInfo
                {
                    DepthTestEnable = true,
                    DepthWriteEnable = !transparent,
                    DepthCompareOp = (VkCompareOp)1,
                };
                var colorBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blend };
                var dynamicState = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStatesPtr };
                var rendering = new VkPipelineRenderingCreateInfo
                {
                    ColorAttachmentCount = 1,
                    ColorAttachmentFormats = colorFormatsPtr,
                    DepthAttachmentFormat = (VkFormat)VulkanContext.DepthFormat,
                };

                var info = new VkGraphicsPipelineCreateInfo
                {
                    Next = &rendering,
                    StageCount = stages.Length,
                    Stages = stagesPtr,
                    VertexInputState = &vertexInput,
                    InputAssemblyState = &inputAssembly,
                    ViewportState = &viewportState,
                    DynamicState = &dynamicState,
                    RasterizationState = &raster,
                    MultisampleState = &multisample,
                    DepthStencilState = &depthState,
                    ColorBlendState = &colorBlend,
                    Layout = _layout,
                };
                return _device.CreateGraphicsPipeline(default, info, null);
            }
        }
    }

    // The sun's camera for shadows: a box ShadowDistance wide on each side of a point a little ahead of the viewer, so
    // the shadows near the camera and the area it looks at are covered. The box moves in whole shadow texels, so
    // shadow edges stay still instead of crawling as the camera moves. Row-vector convention, like DrawModel.
    private Matrix4x4 ComputeLightViewProjection(Vector3 sunDirection)
    {
        float extent = _shadowDistance;
        var towardSun = sunDirection.LengthSquared() > 1e-12f ? Vector3.Normalize(sunDirection) : Vector3.UnitY;
        var up = MathF.Abs(towardSun.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        // Which way the viewer looks, from the inverse of their view-projection (the middle of the near and far planes).
        var forward = -Vector3.UnitZ;
        if (Matrix4x4.Invert(_viewProjection, out var inverse))
        {
            var near = Vector4.Transform(new Vector4(0f, 0f, 0f, 1f), inverse);
            var far = Vector4.Transform(new Vector4(0f, 0f, 1f, 1f), inverse);
            var direction = new Vector3(far.X, far.Y, far.Z) / far.W - new Vector3(near.X, near.Y, near.Z) / near.W;
            if (direction.LengthSquared() > 1e-12f && float.IsFinite(direction.X)) forward = Vector3.Normalize(direction);
        }
        var center = _eye + forward * (extent * 0.5f);

        // Snap the center to the shadow map's texel grid, measured in the sun's view.
        var orientation = Matrix4x4.CreateLookAt(Vector3.Zero, -towardSun, up);
        var inSunView = Vector3.Transform(center, orientation);
        float texel = extent * 2f / _shadowSize;
        inSunView.X = MathF.Round(inSunView.X / texel) * texel;
        inSunView.Y = MathF.Round(inSunView.Y / texel) * texel;
        Matrix4x4.Invert(orientation, out var fromSunView);
        center = Vector3.Transform(inSunView, fromSunView);

        // The sun's camera sits well back, so tall things between it and the area still cast shadows into it.
        float back = extent * 2f + 50f;
        var view = Matrix4x4.CreateLookAt(center + towardSun * back, center, up);
        var projection = Matrix4x4.CreateOrthographic(extent * 2f, extent * 2f, 0.1f, back * 2f);
        _shadowTexelDepth = texel / (back * 2f - 0.1f);
        return view * projection;
    }

    // Renders this frame's queued models into its shadow map, from the sun. Recorded before the scene pass.
    private unsafe void RenderShadowMap(CommandBuffer cmd, int slot)
    {
        VkUtil.Transition(cmd, _shadowImages[slot], 0x2, 0, VulkanContext.LayoutDepthAttachment, 0x1, 0x100, 0, 0x400);

        var depth = new VkRenderingAttachmentInfo
        {
            ImageView = _shadowViews[slot],
            ImageLayout = (VkImageLayout)VulkanContext.LayoutDepthAttachment,
            LoadOp = (VkAttachmentLoadOp)1,
            StoreOp = (VkAttachmentStoreOp)0,   // STORE: the scene pass reads this depth afterwards
            ClearValue = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 1f, Stencil = 0 } },
        };
        var renderInfo = new VkRenderingInfo
        {
            RenderArea = new VkRect2D { Extent = new VkExtent2D { Width = _shadowSize, Height = _shadowSize } },
            LayerCount = 1,
            DepthAttachment = &depth,
        };
        cmd.BeginRendering(in renderInfo);
        cmd.BindPipeline((VkPipelineBindPoint)0, _shadowPipeline);
        cmd.SetViewport(0, new[] { new VkViewport { Width = _shadowSize, Height = _shadowSize, MinDepth = 0, MaxDepth = 1 } });
        cmd.SetScissor(0, new[] { new VkRect2D { Extent = new VkExtent2D { Width = _shadowSize, Height = _shadowSize } } });

        // With shadows off the map is only cleared: nothing reads it then.
        foreach (var draw in _shadowsOn ? _draws : [])
        {
            var meshes = _meshes.Get(draw.Key);
            for (int i = 0; i < meshes.Length; i++)
            {
                var mesh = meshes[i];
                if (IsTransparent(draw, mesh)) continue;   // see-through surfaces don't cast shadows
                WriteMatrix(_push, 0, MeshWorld(draw, mesh, i) * _lightViewProjection);
                cmd.PushConstants(_layout, (VkShaderStageFlags)StageVertexFragment, default, _push);
                cmd.BindVertexBuffer(0, VertexBufferFor(draw, mesh, i), default);
                cmd.BindIndexBuffer(mesh.IndexBuffer, (ByteSize64)0, (VkIndexType)1);
                cmd.DrawIndexed(mesh.IndexCount, 1, 0, 0, 0);
            }
        }

        cmd.EndRendering();
        // The scene pass reads the map in its fragment shader, so the depth writes must finish first.
        VkUtil.Transition(cmd, _shadowImages[slot], 0x2, VulkanContext.LayoutDepthAttachment, 5, 0x200, 0x80, 0x400, 0x20);
    }

    // Depth-only pipeline for the shadow pass. Same vertex shader and layout; no fragment stage or color output.
    private unsafe VkPipeline CreateShadowPipeline()
    {
        var vert = _shaders.Get("pbr.vert", Expand(VertexGlsl), ShaderKind.VertexShader);
        byte[] mainName = "main\0"u8.ToArray();
        var bindings = new[]
        {
            new VkVertexInputBindingDescription { Binding = 0, Stride = (ByteSize32)VertexStride, InputRate = (VkVertexInputRate)0 },
        };
        var attributes = new[]
        {
            new VkVertexInputAttributeDescription { Location = 0, Binding = 0, Format = (VkFormat)106, Offset = (ByteSize32)0 },
            new VkVertexInputAttributeDescription { Location = 1, Binding = 0, Format = (VkFormat)106, Offset = (ByteSize32)12 },
            new VkVertexInputAttributeDescription { Location = 2, Binding = 0, Format = (VkFormat)103, Offset = (ByteSize32)24 },
            new VkVertexInputAttributeDescription { Location = 3, Binding = 0, Format = (VkFormat)109, Offset = (ByteSize32)32 },
        };
        var viewport = new VkViewport { Width = _shadowSize, Height = _shadowSize, MinDepth = 0, MaxDepth = 1 };
        var scissor = new VkRect2D { Extent = new VkExtent2D { Width = _shadowSize, Height = _shadowSize } };
        var dynamicStates = new[] { (VkDynamicState)0, (VkDynamicState)1 };

        fixed (byte* entry = mainName)
        fixed (VkVertexInputBindingDescription* bindingsPtr = bindings)
        fixed (VkVertexInputAttributeDescription* attributesPtr = attributes)
        fixed (VkDynamicState* dynamicStatesPtr = dynamicStates)
        {
            var stage = new VkPipelineShaderStageCreateInfo { Stage = (VkShaderStageFlags)0x1, Module = vert, Name = entry };
            var vertexInput = new VkPipelineVertexInputStateCreateInfo
            {
                VertexBindingDescriptionCount = 1,
                VertexBindingDescriptions = bindingsPtr,
                VertexAttributeDescriptionCount = attributes.Length,
                VertexAttributeDescriptions = attributesPtr,
            };
            var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = (VkPrimitiveTopology)3 };
            var viewportState = new VkPipelineViewportStateCreateInfo
            {
                ViewportCount = 1, Viewports = &viewport,
                ScissorCount = 1, Scissors = &scissor,
            };
            var raster = new VkPipelineRasterizationStateCreateInfo
            {
                PolygonMode = (VkPolygonMode)0,
                CullMode = (VkCullModeFlags)0,
                FrontFace = (VkFrontFace)0,
                LineWidth = 1f,
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = (VkSampleCountFlags)1 };
            var depthState = new VkPipelineDepthStencilStateCreateInfo
            {
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = (VkCompareOp)1,
            };
            var dynamicState = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStatesPtr };
            var rendering = new VkPipelineRenderingCreateInfo
            {
                ColorAttachmentCount = 0,
                DepthAttachmentFormat = (VkFormat)VulkanContext.DepthFormat,
            };

            var info = new VkGraphicsPipelineCreateInfo
            {
                Next = &rendering,
                StageCount = 1,
                Stages = &stage,
                VertexInputState = &vertexInput,
                InputAssemblyState = &inputAssembly,
                ViewportState = &viewportState,
                DynamicState = &dynamicState,
                RasterizationState = &raster,
                MultisampleState = &multisample,
                DepthStencilState = &depthState,
                Layout = _layout,
            };
            return _device.CreateGraphicsPipeline(default, info, null);
        }
    }

    // Remakes the shadow maps at a new size and points the frame sets at them. Waits for the GPU, so it costs a moment;
    // it only happens when the scene's shadow resolution changes.
    private unsafe void ResizeShadowMaps(int size)
    {
        _ctx.WaitIdle();
        for (int i = 0; i < VulkanContext.FramesInFlight; i++)
        {
            _device.DestroyImageView(_shadowViews[i], null);
            _device.DestroyImage(_shadowImages[i], null);
            _device.FreeMemory(_shadowMemories[i], null);
        }
        _shadowSize = size;
        CreateShadowImages(_ctx.Physical);
        for (int i = 0; i < VulkanContext.FramesInFlight; i++)
        {
            var imageInfo = new VkDescriptorImageInfo { ImageView = _shadowViews[i], Sampler = _shadowSampler, ImageLayout = (VkImageLayout)5 };
            var write = new VkWriteDescriptorSet
            {
                DstSet = _shadowSets[i],
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = (VkDescriptorType)1,
                ImageInfo = &imageInfo,
            };
            _device.UpdateDescriptorSets(new[] { write }, default);
        }
    }

    // One depth image, view, and descriptor per frame in flight, plus a shared sampler.
    private void CreateShadowResources(PhysicalDevice physical)
    {
        var samplerInfo = new VkSamplerCreateInfo
        {
            MagFilter = (VkFilter)0,   // nearest: depth textures are not always linearly filterable
            MinFilter = (VkFilter)0,
            AddressModeU = (VkSamplerAddressMode)2,
            AddressModeV = (VkSamplerAddressMode)2,
            AddressModeW = (VkSamplerAddressMode)2,
        };
        _shadowSampler = _device.CreateSampler(in samplerInfo, null);
        CreateShadowImages(physical);
    }

    // The shadow map images and views, one per frame in flight, at the current size.
    private void CreateShadowImages(PhysicalDevice physical)
    {
        for (int i = 0; i < VulkanContext.FramesInFlight; i++)
        {
            var imageInfo = new VkImageCreateInfo
            {
                ImageType = (VkImageType)1,
                Format = (VkFormat)VulkanContext.DepthFormat,
                Extent = new VkExtent3D { Width = _shadowSize, Height = _shadowSize, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = (VkSampleCountFlags)1,
                Tiling = (VkImageTiling)0,
                Usage = (VkImageUsageFlags)(0x20 | 0x4),   // depth attachment + sampled
                SharingMode = (VkSharingMode)0,
                InitialLayout = (VkImageLayout)0,
            };
            _shadowImages[i] = _device.CreateImage(in imageInfo, null);
            _shadowMemories[i] = _device.AllocateMemory(physical, _shadowImages[i], (VkMemoryPropertyFlags)1, null);
            _device.BindImageMemory(_shadowImages[i], _shadowMemories[i], (ByteSize64)0);

            var viewInfo = new VkImageViewCreateInfo
            {
                Image = _shadowImages[i],
                ViewType = (VkImageViewType)1,
                Format = (VkFormat)VulkanContext.DepthFormat,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = (VkImageAspectFlags)0x2,   // depth
                    LevelCount = 1,
                    LayerCount = 1,
                },
            };
            _shadowViews[i] = _device.CreateImageView(in viewInfo, null);
        }
    }

    // Set 0, read by the fragment shaders: the shadow map (a combined image sampler), the frame data (a uniform
    // buffer) and the sky panorama (another sampler).
    private unsafe VkDescriptorSetLayout CreateShadowSetLayout()
    {
        var bindings = new[]
        {
            new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = (VkDescriptorType)1, DescriptorCount = 1, StageFlags = (VkShaderStageFlags)0x10 },
            new VkDescriptorSetLayoutBinding { Binding = 1, DescriptorType = (VkDescriptorType)6, DescriptorCount = 1, StageFlags = (VkShaderStageFlags)0x10 },
            new VkDescriptorSetLayoutBinding { Binding = 2, DescriptorType = (VkDescriptorType)1, DescriptorCount = 1, StageFlags = (VkShaderStageFlags)0x10 },
        };
        fixed (VkDescriptorSetLayoutBinding* b = bindings)
        {
            var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = bindings.Length, Bindings = b };
            return _device.CreateDescriptorSetLayout(in info, null);
        }
    }

    // Allocates one set per frame in flight and points each at its shadow map.
    private unsafe void CreateDescriptors()
    {
        // Each set holds two samplers (shadow map, panorama) and one uniform buffer (frame data).
        var poolSizes = new[]
        {
            new VkDescriptorPoolSize { Type = (VkDescriptorType)1, DescriptorCount = VulkanContext.FramesInFlight * 2 },
            new VkDescriptorPoolSize { Type = (VkDescriptorType)6, DescriptorCount = VulkanContext.FramesInFlight },
        };
        VkDescriptorPool pool;
        fixed (VkDescriptorPoolSize* sizes = poolSizes)
        {
            var poolInfo = new VkDescriptorPoolCreateInfo { MaxSets = VulkanContext.FramesInFlight, PoolSizeCount = poolSizes.Length, PoolSizes = sizes };
            pool = _device.CreateDescriptorPool(in poolInfo, null);
        }
        _descriptorPool = pool;

        // The panorama sampler wraps around horizontally and clamps at the poles, as an equirectangular image needs.
        var panoramaSamplerInfo = new VkSamplerCreateInfo
        {
            MagFilter = (VkFilter)1,
            MinFilter = (VkFilter)1,
            MipmapMode = (VkSamplerMipmapMode)1,
            AddressModeU = (VkSamplerAddressMode)0,
            AddressModeV = (VkSamplerAddressMode)2,
            AddressModeW = (VkSamplerAddressMode)2,
            MaxLod = 16f,
        };
        _panoramaSampler = _device.CreateSampler(in panoramaSamplerInfo, null);
        _blackPanorama = GpuTexture.Solid(_ctx, 0, 0, 0, 255);

        for (int i = 0; i < VulkanContext.FramesInFlight; i++)
        {
            var layout = _shadowSetLayout;
            var allocInfo = new VkDescriptorSetAllocateInfo
            {
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                SetLayouts = &layout,
            };
            VkDescriptorSet set;
            var allocate = (delegate* unmanaged<VkDevice, VkDescriptorSetAllocateInfo*, VkDescriptorSet*, int>)
                _ctx.DeviceEx.GetProcAddr("vkAllocateDescriptorSets"u8).Invoke;
            int result = allocate(_device.Handle, &allocInfo, &set);
            if (result != 0)
                throw new InvalidOperationException($"vkAllocateDescriptorSets failed with {result}");
            _shadowSets[i] = set;

            var imageInfo = new VkDescriptorImageInfo
            {
                ImageView = _shadowViews[i],
                Sampler = _shadowSampler,
                ImageLayout = (VkImageLayout)5,   // shader read only
            };
            var write = new VkWriteDescriptorSet
            {
                DstSet = _shadowSets[i],
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = (VkDescriptorType)1,
                ImageInfo = &imageInfo,
            };

            (_frameBuffers[i], _frameMemories[i]) = VkUtil.CreateHostBuffer(_device, _ctx.Physical, (VkBufferUsageFlags)0x10,
                (ulong)(FrameFloats * sizeof(float)));
            var bufferInfo = new VkDescriptorBufferInfo
            {
                Buffer = _frameBuffers[i],
                Offset = (ByteSize64)0,
                Range = (ByteSize64)(ulong)(FrameFloats * sizeof(float)),
            };
            var frameWrite = new VkWriteDescriptorSet
            {
                DstSet = _shadowSets[i],
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = (VkDescriptorType)6,
                BufferInfo = &bufferInfo,
            };
            _device.UpdateDescriptorSets(new[] { write, frameWrite }, default);
            WritePanorama(_shadowSets[i], _blackPanorama);
        }
    }

    // Points a frame set's sky binding at a texture.
    private unsafe void WritePanorama(VkDescriptorSet set, GpuTexture texture)
    {
        var imageInfo = new VkDescriptorImageInfo { ImageView = texture.View, Sampler = _panoramaSampler, ImageLayout = (VkImageLayout)5 };
        var write = new VkWriteDescriptorSet
        {
            DstSet = set,
            DstBinding = 2,
            DescriptorCount = 1,
            DescriptorType = (VkDescriptorType)1,
            ImageInfo = &imageInfo,
        };
        _device.UpdateDescriptorSets(new[] { write }, default);
    }

    // Sets this frame's sky, ambient light and extra lights. A new panorama image is loaded and uploaded here, which
    // waits for the GPU, so changing the sky costs a moment; frames with the same sky cost nothing extra.
    public void SetLighting(EnvironmentData environment, IReadOnlyList<LightData> lights, Func<string, ImageData>? loadImage = null)
    {
        _environment = environment;
        _lights = lights;

        string wanted = environment.Sky == SkyMode.Panorama ? environment.Panorama : "";
        if (wanted == _panoramaKey) return;

        _ctx.WaitIdle();   // frames in flight may still be reading the old panorama
        var old = _panorama;
        _panorama = null;
        _panoramaKey = wanted;
        if (wanted.Length > 0 && loadImage is not null)
        {
            try
            {
                _panorama = GpuTexture.Create(_ctx, loadImage(wanted).BuildMips(MaxPanoramaWidth));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not load the sky panorama {wanted}: {ex.Message}");
            }
        }
        foreach (var set in _shadowSets)
            WritePanorama(set, _panorama ?? _blackPanorama);
        old?.Dispose();
    }

    // Packs the ambient light, sky and lights into this frame's buffer.
    private void WriteFrameData(int slot)
    {
        var d = _frameData;
        var e = _environment;
        d[0] = e.Ambient.X; d[1] = e.Ambient.Y; d[2] = e.Ambient.Z; d[3] = 0;

        // A panorama that hasn't loaded (or failed to) falls back to the flat sky color.
        bool panorama = e.Sky == SkyMode.Panorama && _panorama is not null;
        int count = Math.Min(_lights.Count, MaxLights);
        d[4] = panorama ? 2 : e.Sky == SkyMode.Gradient ? 1 : 0;
        d[5] = e.SkyIntensity;
        d[6] = count;
        d[7] = _panorama?.MipLevels ?? 1;
        d[8] = panorama && _panorama!.IsHdr ? 1 : 0;
        d[9] = e.SkyLighting;
        d[10] = 0; d[11] = 0;
        d[12] = e.Zenith.X; d[13] = e.Zenith.Y; d[14] = e.Zenith.Z; d[15] = 0;
        d[16] = e.Horizon.X; d[17] = e.Horizon.Y; d[18] = e.Horizon.Z; d[19] = 0;
        d[20] = e.Ground.X; d[21] = e.Ground.Y; d[22] = e.Ground.Z; d[23] = 0;

        for (int i = 0; i < count; i++)
        {
            var l = _lights[i];
            int o = 24 + i * 16;
            var at = l.Type == LightType.Directional ? l.Direction : l.Position;
            d[o + 0] = at.X; d[o + 1] = at.Y; d[o + 2] = at.Z; d[o + 3] = (int)l.Type;
            d[o + 4] = l.Radiance.X; d[o + 5] = l.Radiance.Y; d[o + 6] = l.Radiance.Z; d[o + 7] = l.Range;
            d[o + 8] = l.Direction.X; d[o + 9] = l.Direction.Y; d[o + 10] = l.Direction.Z; d[o + 11] = l.CosInner;
            d[o + 12] = l.CosOuter; d[o + 13] = 0; d[o + 14] = 0; d[o + 15] = 0;
        }
        WriteMatrix(d, FrameLightVp, _lightViewProjection);
        d[FrameShadow] = _shadowsOn ? _shadowStrength : 0f;
        d[FrameShadow + 1] = _shadowTexelDepth;
        d[FrameShadow + 2] = _shadowDistance * 2f / _shadowSize;
        d[FrameShadow + 3] = 0;
        _device.WriteMemory<float>(d, _frameMemories[slot], default, default);
    }

    // The sky pipeline for the scene pass's sample count: a full-screen triangle with no depth test or write.
    private VkPipeline SkyPipelineFor(int samples)
    {
        if (!_skyPipelines.TryGetValue(samples, out var pipeline))
            _skyPipelines[samples] = pipeline = CreateSkyPipeline(samples);
        return pipeline;
    }

    private unsafe VkPipeline CreateSkyPipeline(int samples)
    {
        var vert = _shaders.Get("sky.vert", SkyVertexGlsl, ShaderKind.VertexShader);
        var frag = _shaders.Get("sky.frag", Expand(SkyFragmentGlsl), ShaderKind.FragmentShader);
        byte[] mainName = "main\0"u8.ToArray();
        var colorFormats = new[] { _ctx.ColorFormat };
        var viewport = new VkViewport { Width = _ctx.Width, Height = _ctx.Height, MinDepth = 0, MaxDepth = 1 };
        var scissor = new VkRect2D { Extent = new VkExtent2D { Width = _ctx.Width, Height = _ctx.Height } };
        var blend = new VkPipelineColorBlendAttachmentState { ColorWriteMask = (VkColorComponentFlags)0xF };
        var dynamicStates = new[] { (VkDynamicState)0, (VkDynamicState)1 };

        fixed (byte* entry = mainName)
        fixed (VkFormat* colorFormatsPtr = colorFormats)
        fixed (VkDynamicState* dynamicStatesPtr = dynamicStates)
        {
            var stages = new[]
            {
                new VkPipelineShaderStageCreateInfo { Stage = (VkShaderStageFlags)0x1, Module = vert, Name = entry },
                new VkPipelineShaderStageCreateInfo { Stage = (VkShaderStageFlags)0x10, Module = frag, Name = entry },
            };
            fixed (VkPipelineShaderStageCreateInfo* stagesPtr = stages)
            {
                var vertexInput = new VkPipelineVertexInputStateCreateInfo();
                var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = (VkPrimitiveTopology)3 };
                var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, Viewports = &viewport, ScissorCount = 1, Scissors = &scissor };
                var raster = new VkPipelineRasterizationStateCreateInfo
                {
                    PolygonMode = (VkPolygonMode)0, CullMode = (VkCullModeFlags)0, FrontFace = (VkFrontFace)0, LineWidth = 1f,
                };
                var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = (VkSampleCountFlags)samples };
                var depthState = new VkPipelineDepthStencilStateCreateInfo { DepthTestEnable = false, DepthWriteEnable = false };
                var colorBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blend };
                var dynamicState = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStatesPtr };
                var rendering = new VkPipelineRenderingCreateInfo
                {
                    ColorAttachmentCount = 1,
                    ColorAttachmentFormats = colorFormatsPtr,
                    DepthAttachmentFormat = (VkFormat)VulkanContext.DepthFormat,
                };
                var info = new VkGraphicsPipelineCreateInfo
                {
                    Next = &rendering,
                    StageCount = stages.Length,
                    Stages = stagesPtr,
                    VertexInputState = &vertexInput,
                    InputAssemblyState = &inputAssembly,
                    ViewportState = &viewportState,
                    DynamicState = &dynamicState,
                    RasterizationState = &raster,
                    MultisampleState = &multisample,
                    DepthStencilState = &depthState,
                    ColorBlendState = &colorBlend,
                    Layout = _layout,
                };
                return _device.CreateGraphicsPipeline(default, info, null);
            }
        }
    }

    public void UploadModel(string key, GlbModel model) => _meshes.Upload(key, model);

    // Frees uploaded models that will not be drawn again. Waits for the GPU first, so call it between frames.
    public void UnloadModels(IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0) return;
        _ctx.WaitIdle();
        foreach (var key in keys)
            _meshes.Remove(key);
    }

    // Replaces an uploaded model with a new version. Waits for the GPU first, so call it between frames.
    public void ReloadModel(string key, GlbModel model)
    {
        _ctx.WaitIdle();
        _meshes.Replace(key, model);
    }

    // Runs inside the scene pass after the engine's models are recorded. Scripts use it to draw with raw Vulkan.
    public Action<CommandBuffer>? AfterSceneDraws { get; set; }

    public void SetShadows(bool enabled, float strength, float distance, int resolution = 2048)
    {
        _maxImageSize ??= (int)_ctx.Physical.GetProperties().Limits.MaxImageDimension2D;
        resolution = Math.Clamp(resolution, 256, Math.Min(8192, _maxImageSize.Value));
        if (resolution != _shadowSize) ResizeShadowMaps(resolution);
        _shadowStrength = Math.Clamp(strength, 0f, 1f);
        _shadowsOn = enabled && _shadowStrength > 0f;
        _shadowDistance = float.IsFinite(distance) ? Math.Clamp(distance, MinShadowDistance, MaxShadowDistance) : 40f;
    }

    public void BeginFrame(Matrix4x4 viewProjection, Vector3 cameraPosition, Vector3 sunDirection, Vector3 sunRadiance)
    {
        _viewProjection = viewProjection;
        _eye = cameraPosition;
        _particles.BeginFrame();
        Gpu.ViewProjection = viewProjection;
        Gpu.CameraPosition = cameraPosition;
        _push[OffCamera + 0] = cameraPosition.X;
        _push[OffCamera + 1] = cameraPosition.Y;
        _push[OffCamera + 2] = cameraPosition.Z;
        _push[OffCamera + 3] = 0;
        _push[OffLight + 0] = sunDirection.X;
        _push[OffLight + 1] = sunDirection.Y;
        _push[OffLight + 2] = sunDirection.Z;
        _push[OffLight + 3] = 0;
        _push[OffRadiance + 0] = sunRadiance.X;
        _push[OffRadiance + 1] = sunRadiance.Y;
        _push[OffRadiance + 2] = sunRadiance.Z;
        _push[OffRadiance + 3] = 0;

        _lightViewProjection = ComputeLightViewProjection(sunDirection);

        // Draws are queued, not recorded, so the shadow pass can use this frame's own positions.
        _draws.Clear();
        _activeSlot = _shadowSlot;
        _shadowSlot = (_shadowSlot + 1) % VulkanContext.FramesInFlight;
        _framePending = true;

        _ctx.BeginFrame(deferScenePass: true);
    }

    public void DrawModel(string key, Matrix4x4 world, Vector4 tint) => DrawModel(key, world, tint, null);

    public void DrawModel(string key, Matrix4x4 world, Vector4 tint, Engine.Core.Animation.ModelPose? pose) =>
        _draws.Add(new QueuedDraw { Key = key, World = world, Tint = tint, Pose = pose });

    // Where a mesh goes in the world: its node's transform (the posed one while animated) then the object's.
    private static Matrix4x4 MeshWorld(QueuedDraw draw, GpuMesh mesh, int index) =>
        (draw.Pose is { } pose && index < pose.MeshTransforms.Length ? pose.MeshTransforms[index] : mesh.RestTransform) * draw.World;

    private static VkBuffer VertexBufferFor(QueuedDraw draw, GpuMesh mesh, int index) =>
        draw.Vertices is { } own && index < own.Length && own[index] is { } bent ? bent : mesh.VertexBuffer;

    // Writes the vertices of every posed mesh queued this frame (animated characters, terrain, water) into this slot's
    // dynamic buffers. A pose with ready-made vertices is copied as it is; otherwise its positions, normals, texture
    // coordinates and colors are put together here, across the CPU's cores for big meshes.
    private void PrepareAnimatedMeshes(int slot)
    {
        var buffers = _dynamicBuffers[slot];
        int used = 0;
        foreach (var draw in _draws)
        {
            if (draw.Pose is not { } pose) continue;
            var meshes = _meshes.Get(draw.Key);
            for (int i = 0; i < meshes.Length && i < pose.Positions.Length; i++)
            {
                var mesh = meshes[i];
                float[] data;
                if (pose.Vertices is { } ready && i < ready.Length && ready[i] is { } vertices &&
                    vertices.Length == mesh.VertexCount * GpuMesh.FloatsPerVertex)
                {
                    data = vertices;
                }
                else
                {
                    var positions = pose.Positions[i];
                    var normals = pose.Normals[i];
                    var colors = pose.Colors is { } ownColors && i < ownColors.Length && ownColors[i] is { } movedColors && movedColors.Length == mesh.VertexCount * 4
                        ? movedColors : mesh.Colors;
                    var texCoords = pose.TexCoords is { } own && i < own.Length && own[i] is { } moved && moved.Length == mesh.TexCoords.Length
                        ? moved : mesh.TexCoords;
                    if (positions is null || normals is null || positions.Length != mesh.VertexCount * 3 || normals.Length != positions.Length)
                        continue;
                    if (!_vertexScratch.TryGetValue(mesh.VertexCount, out var scratch))
                        _vertexScratch[mesh.VertexCount] = scratch = new float[mesh.VertexCount * GpuMesh.FloatsPerVertex];
                    Interleave(scratch, mesh.VertexCount, positions, normals, texCoords, colors);
                    data = scratch;
                }

                ulong bytes = (ulong)(data.Length * sizeof(float));
                if (used == buffers.Count)
                {
                    var (b, m) = VkUtil.CreateHostBuffer(_device, _ctx.Physical, (VkBufferUsageFlags)0x80, bytes);
                    buffers.Add(new DynamicBuffer { Buffer = b, Memory = m, Size = bytes });
                }
                else if (buffers[used].Size < bytes)
                {
                    // This slot's last frame has finished, so the old buffer is no longer in use.
                    _device.DestroyBuffer(buffers[used].Buffer, null);
                    _device.FreeMemory(buffers[used].Memory, null);
                    var (b, m) = VkUtil.CreateHostBuffer(_device, _ctx.Physical, (VkBufferUsageFlags)0x80, bytes);
                    buffers[used] = new DynamicBuffer { Buffer = b, Memory = m, Size = bytes };
                }

                _device.WriteMemory<float>(data, buffers[used].Memory, default, default);
                draw.Vertices ??= new VkBuffer?[meshes.Length];
                draw.Vertices[i] = buffers[used].Buffer;
                used++;
            }
        }
    }

    // Puts separate vertex arrays together in the GPU's layout: position, normal, texture coordinates, color. Big meshes
    // are done in chunks across the CPU's cores.
    private static void Interleave(float[] data, int vertexCount, float[] positions, float[] normals, float[] texCoords, float[]? colors)
    {
        int chunks = vertexCount >= 32768 ? Environment.ProcessorCount * 4 : 1;
        int chunkSize = (vertexCount + chunks - 1) / chunks;
        Parallel.For(0, chunks, chunk =>
        {
            for (int v = chunk * chunkSize, end = Math.Min(vertexCount, v + chunkSize); v < end; v++)
            {
                int d = v * GpuMesh.FloatsPerVertex, p = v * 3, t = v * 2;
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

    public void DrawParticles(ReadOnlySpan<ParticleInstance> particles, ParticleBlend blend, ParticleSprite sprite) =>
        _particles.Add(particles, blend, sprite, _eye);

    // Records one queued model into the scene pass.
    // True when a mesh is see-through in this draw: its material blends, or the tint or the material color isn't fully opaque.
    private static bool IsTransparent(QueuedDraw draw, GpuMesh mesh) =>
        mesh.Material.AlphaBlend || draw.Tint.W * mesh.Material.BaseColor[3] < 0.999f;

    // Records the solid meshes of one queued model into the scene pass, and adds its see-through meshes to the list
    // drawn afterwards.
    private void RecordModel(QueuedDraw draw, List<(QueuedDraw Draw, int Mesh, float Distance)> transparent)
    {
        var meshes = _meshes.Get(draw.Key);
        for (int meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
        {
            var mesh = meshes[meshIndex];
            if (IsTransparent(draw, mesh))
            {
                var at = MeshWorld(draw, mesh, meshIndex).Translation;
                transparent.Add((draw, meshIndex, Vector3.DistanceSquared(at, _eye)));
                continue;
            }
            RecordMesh(draw, mesh, meshIndex);
        }
    }

    // One mesh: its matrices and material in the push constants, its textures, then the draw.
    private void RecordMesh(QueuedDraw draw, GpuMesh mesh, int meshIndex)
    {
        var cmd = _ctx.CurrentCommandBuffer;
        var tint = draw.Tint;
        var world = MeshWorld(draw, mesh, meshIndex);
        WriteMatrix(_push, 0, world * _viewProjection);
        WriteMatrix(_push, OffModel, world);
        var material = mesh.Material;
        _push[OffColor + 0] = tint.X * material.BaseColor[0];
        _push[OffColor + 1] = tint.Y * material.BaseColor[1];
        _push[OffColor + 2] = tint.Z * material.BaseColor[2];
        _push[OffColor + 3] = tint.W * material.BaseColor[3];
        _push[OffMaterial + 0] = material.Roughness;
        _push[OffMaterial + 1] = material.Metallic;
        _push[OffMaterial + 2] = material.Ior;
        _push[OffMaterial + 3] = material.AlphaCutoff;
        _push[OffSpecular + 0] = material.SpecularColor[0];
        _push[OffSpecular + 1] = material.SpecularColor[1];
        _push[OffSpecular + 2] = material.SpecularColor[2];
        _push[OffSpecular + 3] = material.SpecularFactor;
        _push[OffEmission + 0] = material.Emission[0];
        _push[OffEmission + 1] = material.Emission[1];
        _push[OffEmission + 2] = material.Emission[2];
        _push[OffEmission + 3] = 0;
        _push[OffExtra + 0] = material.OcclusionImage is null ? 0f : material.OcclusionStrength;
        _push[OffExtra + 1] = material.NormalImage is null ? 0f : MathF.Max(material.NormalScale, 0.0001f);
        cmd.PushConstants(_layout, (VkShaderStageFlags)StageVertexFragment, default, _push);
        cmd.BindDescriptorSets((VkPipelineBindPoint)0, _layout, 1, new[] { mesh.MaterialSet }, default);

        cmd.BindVertexBuffer(0, VertexBufferFor(draw, mesh, meshIndex), default);
        cmd.BindIndexBuffer(mesh.IndexBuffer, (ByteSize64)0, (VkIndexType)1);
        cmd.DrawIndexed(mesh.IndexCount, 1, 0, 0, 0);
    }

    // Runs just before the frame ends, when every DrawModel call is known. Records the shadow pass from this
    // frame's models, then opens the scene pass and draws the same models.
    private void FlushFrame()
    {
        if (!_framePending) return;
        _framePending = false;

        var cmd = _ctx.CurrentCommandBuffer;
        PrepareAnimatedMeshes(_activeSlot);
        RenderShadowMap(cmd, _activeSlot);

        // This slot's last frame has finished (BeginFrame waited for it), so its buffer is free to rewrite.
        WriteFrameData(_activeSlot);

        _ctx.BeginScenePass();

        // The sky goes first, behind everything: a full-screen triangle with no depth test.
        cmd.BindPipeline((VkPipelineBindPoint)0, SkyPipelineFor(_ctx.SampleCount));
        cmd.BindDescriptorSets((VkPipelineBindPoint)0, _layout, 0, new[] { _shadowSets[_activeSlot] }, default);
        Matrix4x4.Invert(_viewProjection, out var inverseViewProjection);
        WriteMatrix(_push, 0, inverseViewProjection);
        cmd.PushConstants(_layout, (VkShaderStageFlags)StageVertexFragment, default, _push);
        cmd.Draw(3, 1, 0, 0);

        cmd.BindPipeline((VkPipelineBindPoint)0, PipelineFor(_ctx.SampleCount));
        cmd.BindDescriptorSets((VkPipelineBindPoint)0, _layout, 0, new[] { _shadowSets[_activeSlot] }, default);
        var transparent = new List<(QueuedDraw Draw, int Mesh, float Distance)>();
        foreach (var draw in _draws)
            RecordModel(draw, transparent);

        // See-through meshes after every solid one, farthest first, so nearer ones blend over farther ones.
        if (transparent.Count > 0)
        {
            transparent.Sort((a, b) => b.Distance.CompareTo(a.Distance));
            cmd.BindPipeline((VkPipelineBindPoint)0, TransparentPipelineFor(_ctx.SampleCount));
            cmd.BindDescriptorSets((VkPipelineBindPoint)0, _layout, 0, new[] { _shadowSets[_activeSlot] }, default);
            foreach (var (draw, meshIndex, _) in transparent)
                RecordMesh(draw, _meshes.Get(draw.Key)[meshIndex], meshIndex);
        }
        _draws.Clear();
        _particles.Record(cmd, _viewProjection);   // see-through sprites go after every solid model
        AfterSceneDraws?.Invoke(cmd);
    }

    public void EndFrame() => _ctx.EndFrame(null);

    // The same, then drawUi records 2D drawing (such as the game's UI) over the finished scene.
    public void EndFrame(Action<CommandBuffer>? drawUi) => _ctx.EndFrame(drawUi);

    // Row-major System.Numerics fields in order. GLSL reads these as column-major (the transpose),
    // so "mvp * v" in the shader matches "v * mvp" in C#.
    private static void WriteMatrix(float[] dst, int offset, Matrix4x4 m)
    {
        dst[offset + 0] = m.M11; dst[offset + 1] = m.M12; dst[offset + 2] = m.M13; dst[offset + 3] = m.M14;
        dst[offset + 4] = m.M21; dst[offset + 5] = m.M22; dst[offset + 6] = m.M23; dst[offset + 7] = m.M24;
        dst[offset + 8] = m.M31; dst[offset + 9] = m.M32; dst[offset + 10] = m.M33; dst[offset + 11] = m.M34;
        dst[offset + 12] = m.M41; dst[offset + 13] = m.M42; dst[offset + 14] = m.M43; dst[offset + 15] = m.M44;
    }

    public void Dispose()
    {
        _ctx.WaitIdle();
        _particles.Dispose();
        _meshes.Dispose();
        foreach (var list in _dynamicBuffers)
            foreach (var dynamic in list)
            {
                _device.DestroyBuffer(dynamic.Buffer, null);
                _device.FreeMemory(dynamic.Memory, null);
            }
        foreach (var pipeline in _pipelines.Values.Concat(_transparentPipelines.Values))
            _device.DestroyPipeline(pipeline, null);
        foreach (var pipeline in _skyPipelines.Values)
            _device.DestroyPipeline(pipeline, null);
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroyPipeline(_shadowPipeline, null);
        _device.DestroyDescriptorPool(_descriptorPool, null);
        _device.DestroyDescriptorSetLayout(_shadowSetLayout, null);
        _device.DestroySampler(_shadowSampler, null);
        _device.DestroySampler(_panoramaSampler, null);
        _panorama?.Dispose();
        _blackPanorama.Dispose();
        _materials.Dispose();
        for (int i = 0; i < VulkanContext.FramesInFlight; i++)
        {
            _device.DestroyBuffer(_frameBuffers[i], null);
            _device.FreeMemory(_frameMemories[i], null);
            _device.DestroyImageView(_shadowViews[i], null);
            _device.DestroyImage(_shadowImages[i], null);
            _device.FreeMemory(_shadowMemories[i], null);
        }
    }
}
