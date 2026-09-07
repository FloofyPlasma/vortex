using VortexEngine.Assets;
using VortexEngine.Audio;
using VortexEngine.Core;
using VortexEngine.Physics;
using VortexEngine.Platform;
using VortexEngine.Rendering;
using VortexEngine.Rendering.Vulkan;

namespace VortexEngine;

public sealed class Engine : IDisposable
{
    public World World { get; }
    public PhysicsSystem Physics { get; }
    public Renderer Renderer { get; }
    public AssetManager Assets { get; }
    public AudioSystem Audio { get; }

    private IPlatformWindow? window;

    public Engine()
    {
        World = new World();
        Physics = new PhysicsSystem(World);
        Renderer = new Renderer();
        Assets = new AssetManager();
        Audio = new AudioSystem();
    }

    public void Initialize(IPlatformWindow platformWindow)
    {
        window = platformWindow;
        
        if (platformWindow is not IVulkanSurfaceProvider surfaceProvider)
            throw new InvalidOperationException("IPlatformWindow must also implement IVulkanSurfaceProvider for Vulkan rendering");
        
        Renderer.Initialize(surfaceProvider, platformWindow.Width, platformWindow.Height);
    }

    public void Update(float dt)
    {
        Physics.Update(dt);
        Audio.Update(dt);
    }

    public RenderScene ExtractRenderScene()
    {
        // TODO:
        return new RenderScene();
    }

    public void Dispose()
    {
        Renderer?.Dispose();
        Audio?.Dispose();
    }
}
