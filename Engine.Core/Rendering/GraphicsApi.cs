namespace Engine.Core.Rendering;

// Which graphics API draws the editor and games. Vulkan works everywhere (on a Mac through MoltenVK, which turns it into Metal).
// Metal draws with Apple's own API directly, and only exists on a Mac.
public enum GraphicsApi { Vulkan, Metal }

public static class GraphicsApis
{
    // Set EMPIRE_GRAPHICS to "metal" or "vulkan" to override the project's choice, for example to compare the two.
    public const string Override = "EMPIRE_GRAPHICS";

    // True where Metal can be used: a Mac (not the iPad port, which draws through MoltenVK).
    public static bool MetalAvailable => OperatingSystem.IsMacOS();

    // The API to draw with: the override when it is set, else the project's choice. Metal falls back to Vulkan where it isn't available.
    public static GraphicsApi Resolve(GraphicsApi chosen, string? overrideValue = null)
    {
        overrideValue ??= Environment.GetEnvironmentVariable(Override);
        if (Enum.TryParse(overrideValue?.Trim(), ignoreCase: true, out GraphicsApi forced)) chosen = forced;
        return chosen == GraphicsApi.Metal && !MetalAvailable ? GraphicsApi.Vulkan : chosen;
    }

    // Reads a saved name ("vulkan", "metal"); anything else is Vulkan.
    public static GraphicsApi Parse(string? name) =>
        Enum.TryParse(name?.Trim(), ignoreCase: true, out GraphicsApi api) && Enum.IsDefined(api) ? api : GraphicsApi.Vulkan;
}
