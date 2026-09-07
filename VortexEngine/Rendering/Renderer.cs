using VortexEngine.Rendering.Vulkan;

namespace VortexEngine.Rendering;

public sealed class Renderer : IDisposable
{
    private VulkanRenderer? vulkanRenderer;
    
    public void Initialize(IVulkanSurfaceProvider surfaceProvider, uint width, uint height)
    {
        vulkanRenderer = new VulkanRenderer(surfaceProvider, width, height);
    }

    public void Render(RenderRequest request)
    {
        vulkanRenderer?.Render();
    }

    public void Present()
    {
    }

    public void Dispose()
    {
        vulkanRenderer?.Dispose();
    }
}
