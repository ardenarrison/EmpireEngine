using System.Runtime.InteropServices;
using Brutal.GlfwApi;

namespace Engine.Brutal.Gfx;

// macOS builds ship their own Vulkan loader and MoltenVK (see EmpireBundleVulkan in Directory.Build.targets).
// Prepare points Vulkan at them, so the editor and games run without a Vulkan SDK or Homebrew. Call it before
// Glfw.Init. When nothing is bundled it does nothing, and the system's Vulkan is used as before.
public static unsafe class BundledVulkan
{
    private static bool _prepared;

    public static void Prepare()
    {
        if (_prepared || !OperatingSystem.IsMacOS()) return;
        _prepared = true;

        string baseDir = AppContext.BaseDirectory;

        // The driver: the loader reads these when the instance is created. A value the user set wins.
        string icd = Path.Combine(baseDir, "vulkan", "icd.d", "MoltenVK_icd.json");
        if (File.Exists(icd) && Environment.GetEnvironmentVariable("VK_DRIVER_FILES") is null &&
            Environment.GetEnvironmentVariable("VK_ICD_FILENAMES") is null)
        {
            // .NET's Environment.SetEnvironmentVariable doesn't reach native code on Unix, so set it in libc.
            setenv("VK_DRIVER_FILES", icd, 1);
            setenv("VK_ICD_FILENAMES", icd, 1);
        }

        // The loader: load the copy next to libVulkanEx (the one BRUTAL's Vulkan bindings link to), then hand it
        // to GLFW, which otherwise searches the system paths by name and finds nothing.
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        string? loaderPath = new[]
            {
                Path.Combine(baseDir, "libvulkan.1.dylib"),
                Path.Combine(baseDir, "runtimes", arch, "native", "libvulkan.1.dylib"),
            }
            .FirstOrDefault(File.Exists);
        if (loaderPath is null) return;

        try
        {
            nint loader = NativeLibrary.Load(loaderPath);
            nint getInstanceProcAddr = NativeLibrary.GetExport(loader, "vkGetInstanceProcAddr");

            // Resolved the same way as BRUTAL's own GLFW imports, so this is the same GLFW library.
            nint glfw = NativeLibrary.Load("glfw", typeof(Glfw).Assembly, null);
            if (NativeLibrary.TryGetExport(glfw, "glfwInitVulkanLoader", out nint initVulkanLoader))
                ((delegate* unmanaged<nint, void>)initVulkanLoader)(getInstanceProcAddr);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not load the bundled Vulkan loader ({loaderPath}): {ex.Message}");
        }
    }

    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern int setenv(string name, string value, int overwrite);
}
