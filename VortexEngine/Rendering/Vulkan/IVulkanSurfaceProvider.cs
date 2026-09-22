using Vortice.Vulkan;

namespace VortexEngine.Rendering.Vulkan;

public interface IVulkanSurfaceProvider
{
    [Obsolete("Use CreateSurface(VkInstance) instead")]
    void CreateSurface(VkInstance instance, out VkSurfaceKHR surface);

    VkSurfaceKHR CreateSurface(VkInstance instance);
    void GetRequiredExtensions(out string[] extensions);
}