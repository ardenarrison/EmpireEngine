using System.Runtime.InteropServices;
using Engine.Brutal.Gfx;

namespace Engine.Brutal.Metal;

// Turns the engine's SPIR-V shaders into Metal Shading Language with SPIRV-Cross (Apache 2.0, ThirdParty/SpirvCross), so the Metal
// renderer draws with the same GLSL as the Vulkan one. Built games carry the SPIR-V (shaders.bin is the same on every platform) and a
// Mac game translates it when it starts.
//
// Where each resource lands in Metal's argument tables, for every shader:
//   push constants                          buffer 0
//   uniform or storage buffer (set, binding)  buffer 1 + set * 8 + binding
//   combined image sampler (set, binding)   texture and sampler set * 8 + binding
//   vertex data (binding 0)                 buffer VertexBufferIndex
//   sizes of storage buffers                buffer BufferSizesIndex, a uint per buffer index
// The vertex stage is flipped in y, because Vulkan's clip space points y down and Metal's points it up; everything then lands on the
// same pixels as with Vulkan, so texture coordinates and shadow maps need no change.
internal static unsafe class SpirvToMsl
{
    public const int PushConstantIndex = 0;
    public const int VertexBufferIndex = 30;
    public const int BufferSizesIndex = 25;
    private const int SetStride = 8;
    private const int MaxSets = 2;

    public static int BufferIndex(int set, int binding) => 1 + set * SetStride + binding;
    public static int TextureIndex(int set, int binding) => set * SetStride + binding;

    private const string Library = "spirv-cross-c-shared";

    private const int BackendMsl = 3;
    private const int CaptureTakeOwnership = 1;
    private const uint OptionFlipVertexY = 4 | 0x1000000;
    private const uint OptionMslVersion = 17 | 0x8000000;
    private const uint OptionMslPlatform = 31 | 0x8000000;
    private const uint OptionMslBufferSizeIndex = 36 | 0x8000000;
    private const uint PlatformMacOS = 1;
    private const uint MslVersion = 2 * 10000 + 3 * 100;   // Metal 2.3, macOS 11
    private const uint PushConstantSet = ~0u;
    private const int ModelVertex = 0, ModelFragment = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ResourceBinding
    {
        public int Stage;
        public uint DescSet;
        public uint Binding;
        public uint Count;
        public uint MslBuffer;
        public uint MslTexture;
        public uint MslSampler;
    }

    [DllImport(Library)] private static extern int spvc_context_create(out nint context);
    [DllImport(Library)] private static extern void spvc_context_destroy(nint context);
    [DllImport(Library)] private static extern nint spvc_context_get_last_error_string(nint context);
    [DllImport(Library)] private static extern int spvc_context_parse_spirv(nint context, uint* spirv, nuint wordCount, out nint ir);
    [DllImport(Library)] private static extern int spvc_context_create_compiler(nint context, int backend, nint ir, int mode, out nint compiler);
    [DllImport(Library)] private static extern int spvc_compiler_create_compiler_options(nint compiler, out nint options);
    [DllImport(Library)] private static extern int spvc_compiler_options_set_uint(nint options, uint option, uint value);
    [DllImport(Library)] private static extern int spvc_compiler_options_set_bool(nint options, uint option, byte value);
    [DllImport(Library)] private static extern int spvc_compiler_install_compiler_options(nint compiler, nint options);
    [DllImport(Library)] private static extern int spvc_compiler_msl_add_resource_binding_2(nint compiler, ResourceBinding* binding);
    [DllImport(Library)] private static extern int spvc_compiler_compile(nint compiler, out nint source);
    [DllImport(Library)] private static extern nint spvc_compiler_get_cleansed_entry_point_name(nint compiler, byte* name, int model);

    // The Metal source of one shader, and the name of its entry point in it.
    public static (string Source, string EntryPoint) Translate(byte[] spirv, ShaderStage stage, string name)
    {
        if (spirv.Length % 4 != 0) throw new InvalidDataException($"The {name} shader isn't valid SPIR-V.");
        Check(spvc_context_create(out nint context), 0, "create a SPIRV-Cross context");
        try
        {
            nint ir;
            fixed (byte* words = spirv)
                Check(spvc_context_parse_spirv(context, (uint*)words, (nuint)(spirv.Length / 4), out ir), context, "read " + name);
            Check(spvc_context_create_compiler(context, BackendMsl, ir, CaptureTakeOwnership, out nint compiler), context, "translate " + name);

            Check(spvc_compiler_create_compiler_options(compiler, out nint options), context, "set up " + name);
            spvc_compiler_options_set_uint(options, OptionMslVersion, MslVersion);
            spvc_compiler_options_set_uint(options, OptionMslPlatform, PlatformMacOS);
            spvc_compiler_options_set_uint(options, OptionMslBufferSizeIndex, BufferSizesIndex);
            spvc_compiler_options_set_bool(options, OptionFlipVertexY, stage == ShaderStage.Vertex ? (byte)1 : (byte)0);
            Check(spvc_compiler_install_compiler_options(compiler, options), context, "set up " + name);

            int model = stage == ShaderStage.Vertex ? ModelVertex : ModelFragment;
            var push = new ResourceBinding
            {
                Stage = model, DescSet = PushConstantSet, Binding = 0, Count = 1,
                MslBuffer = PushConstantIndex, MslTexture = PushConstantIndex, MslSampler = PushConstantIndex,
            };
            spvc_compiler_msl_add_resource_binding_2(compiler, &push);
            for (int set = 0; set < MaxSets; set++)
                for (int binding = 0; binding < SetStride; binding++)
                {
                    var map = new ResourceBinding
                    {
                        Stage = model, DescSet = (uint)set, Binding = (uint)binding, Count = 1,
                        MslBuffer = (uint)BufferIndex(set, binding),
                        MslTexture = (uint)TextureIndex(set, binding),
                        MslSampler = (uint)TextureIndex(set, binding),
                    };
                    spvc_compiler_msl_add_resource_binding_2(compiler, &map);
                }

            Check(spvc_compiler_compile(compiler, out nint source), context, "translate " + name);
            var main = "main\0"u8;
            nint entry;
            fixed (byte* m = main)
                entry = spvc_compiler_get_cleansed_entry_point_name(compiler, m, model);
            return (Marshal.PtrToStringUTF8(source)!, Marshal.PtrToStringUTF8(entry) ?? "main0");
        }
        finally
        {
            spvc_context_destroy(context);
        }
    }

    private static void Check(int result, nint context, string what)
    {
        if (result == 0) return;
        string detail = context != 0 ? Marshal.PtrToStringUTF8(spvc_context_get_last_error_string(context)) ?? "" : "";
        throw new InvalidOperationException($"Could not {what} for Metal (SPIRV-Cross error {result}). {detail}".TrimEnd());
    }
}
