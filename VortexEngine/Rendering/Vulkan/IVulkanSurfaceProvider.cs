using Vortice.Vulkan;

namespace VortexEngine.Rendering.Vulkan;

public interface IVulkanSurfaceProvider
{
    void CreateSurface(VkInstance instance, out VkSurfaceKHR surface);
    void GetRequiredExtensions(out string[] extensions);
}