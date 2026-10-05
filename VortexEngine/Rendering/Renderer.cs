using VortexEngine.Rendering.Vulkan;

namespace VortexEngine.Rendering;

public sealed class Renderer : IDisposable
{
    private VulkanRenderer? vulkanRenderer;

    public void Dispose()
    {
        vulkanRenderer?.Dispose();
        vulkanRenderer = null;
    }

    public void Initialize(IVulkanSurfaceProvider surfaceProvider, uint width, uint height)
    {
        if (vulkanRenderer is not null)
            throw new InvalidOperationException("Renderer is already initialized");

        vulkanRenderer = new VulkanRenderer(surfaceProvider, width, height);
    }

    public void UpdateViewport(uint width, uint height)
    {
        vulkanRenderer?.UpdateViewport(width, height);
    }

    public MeshHandle LoadMesh(byte[] meshData)
    {
        return RequireRenderer().LoadMesh(meshData);
    }

    public CubemapHandle? LoadEquirectangularHDRI(byte[] hdrData, uint width, uint height)
    {
        return RequireRenderer().LoadEquirectangularHDRI(hdrData, width, height);
    }

    private VulkanRenderer RequireRenderer()
    {
        return vulkanRenderer
               ?? throw new InvalidOperationException("Renderer has not been initialized; call Initialize first");
    }

    public void Render(RenderRequest request)
    {
        vulkanRenderer?.Render(request);
    }

    public void Present()
    {
    }
}

public struct MeshHandle
{
    public uint Id { get; }
    internal MeshHandle(uint id) => Id = id;
}

public struct TextureHandle
{
    public uint Id { get; }
    internal TextureHandle(uint id) => Id = id;
}

public struct MaterialHandle
{
    public uint Id { get; }
    internal MaterialHandle(uint id) => Id = id;
}

public struct CubemapHandle
{
    public uint Id { get; }
    internal CubemapHandle(uint id) => Id = id;
}
