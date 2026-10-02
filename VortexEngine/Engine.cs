using System.Numerics;
using VortexEngine.Assets;
using VortexEngine.Audio;
using VortexEngine.Components;
using VortexEngine.Core;
using VortexEngine.Physics;
using VortexEngine.Platform;
using VortexEngine.Rendering;
using VortexEngine.Rendering.Vulkan;

namespace VortexEngine;

public sealed class Engine : IDisposable
{
    private IPlatformWindow? window;

    public Engine()
    {
        World = new World();
        Physics = new PhysicsSystem(World);
        Renderer = new Renderer();
        Assets = new AssetManager();
        Audio = new AudioSystem();
    }

    public World World { get; }
    public PhysicsSystem Physics { get; }
    public Renderer Renderer { get; }
    public AssetManager Assets { get; }
    public AudioSystem Audio { get; }

    public void Dispose()
    {
        if (window != null)
        {
            window.OnResize -= (w, h) => Renderer.UpdateViewport(w, h);
            window.OnKeyDown -= HandleKeyDown;
            window.OnKeyUp -= HandleKeyUp;
            window.OnMouseMotion -= HandleMouseMotion;
            window.OnMouseButtonDown -= HandleMouseButtonDown;
            window.OnMouseButtonUp -= HandleMouseButtonUp;
            window.OnClosing -= HandleWindowClosing;
        }

        Renderer?.Dispose();
        Audio?.Dispose();
    }

    public void Initialize(IPlatformWindow platformWindow)
    {
        window = platformWindow;

        if (platformWindow is not IVulkanSurfaceProvider surfaceProvider)
            throw new InvalidOperationException(
                "IPlatformWindow must also implement IVulkanSurfaceProvider for Vulkan rendering");

        Renderer.Initialize(surfaceProvider, platformWindow.Width, platformWindow.Height);

        window.OnResize += (w, h) => Renderer.UpdateViewport(w, h);
        window.OnKeyDown += HandleKeyDown;
        window.OnKeyUp += HandleKeyUp;
        window.OnMouseMotion += HandleMouseMotion;
        window.OnMouseButtonDown += HandleMouseButtonDown;
        window.OnMouseButtonUp += HandleMouseButtonUp;
        window.OnClosing += HandleWindowClosing;
    }

    private void HandleKeyDown(IKeyEvent key)
    {
    }

    private void HandleKeyUp(IKeyEvent key)
    {
    }

    private void HandleMouseMotion(IMouseMotionEvent motion)
    {
    }

    private void HandleMouseButtonDown(IMouseButtonEvent button)
    {
    }

    private void HandleMouseButtonUp(IMouseButtonEvent button)
    {
    }

    private void HandleWindowClosing()
    {
    }

    public void Update(float dt)
    {
        Physics.Update(dt);
        Audio.Update(dt);
    }

    public RenderRequest ExtractRenderScene()
    {
        if (window is null)
        {
            return new RenderRequest();
        }

        var cameraEntity = World.EntitiesWith(typeof(Camera)).FirstOrDefault();
        var camera = cameraEntity != default && World.TryGetComponent(cameraEntity, out Camera cam)
            ? cam
            : new Camera
            {
                Position = new Vector3(0, 15, 10),
                Target = Vector3.Zero,
                Up = Vector3.UnitY,
            };

        var lightEntity = World.EntitiesWith(typeof(DirectionalLight)).FirstOrDefault();
        var light = lightEntity != default && World.TryGetComponent(lightEntity, out DirectionalLight dirLight)
            ? dirLight
            : new DirectionalLight
            {
                Direction = new Vector3(0, -2.5f, -3.5f),
                Color = new Vector3(0.8f, 0.8f, 0.8f),
                Intensity = 1.0f,
            };

        var meshes = new List<RenderMesh>();
        foreach (var entity in World.EntitiesWith(typeof(Transform), typeof(MeshRenderer)))
        {
            if (World.TryGetComponent(entity, out Transform transform) &&
                World.TryGetComponent(entity, out MeshRenderer renderer))
            {
                meshes.Add(new RenderMesh
                {
                    Handle = renderer.MeshHandle,
                    Transform = transform.GetMatrix(),
                });
            }
        }

        return new RenderRequest
        {
            ViewportWidth = window.Width,
            ViewportHeight = window.Height,
            Camera = camera,
            Light = light,
            Meshes = meshes,
        };
    }

    public void Render()
    {
        Renderer.Render(ExtractRenderScene());
    }
}