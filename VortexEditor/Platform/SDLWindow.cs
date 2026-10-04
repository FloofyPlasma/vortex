using SDL3;
using VortexEngine.Platform;
using VortexEngine.Rendering.Vulkan;
using Vortice.Vulkan;

namespace VortexEditor.Platform;

// ReSharper disable once InconsistentNaming
public class SDLWindow : IPlatformWindow, IVulkanSurfaceProvider
{
    private bool isOpen;
    private IntPtr window;

    public SDLWindow(uint width, uint height, string title = "Vortex Editor")
    {
        SDL.Init(SDL.InitFlags.Video);

        window = SDL.CreateWindow(title, (int)width, (int)height, SDL.WindowFlags.Vulkan);

        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create SDL window");
        }

        Width = width;
        Height = height;
        isOpen = true;
    }

    public nint NativeHandle => (nint)window;
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public bool IsOpen => isOpen;

    public void ProcessEvents()
    {
        while (SDL.PollEvent(out var evt))
        {
            switch (evt.Type)
            {
                case (uint)SDL.EventType.Quit:
                    isOpen = false;
                    OnClosing?.Invoke();
                    break;

                case (uint)SDL.EventType.KeyDown:
                    OnKeyDown?.Invoke(new KeyEvent { KeyCode = (int)evt.Key.Key, Repeat = evt.Key.Repeat });
                    break;

                case (uint)SDL.EventType.KeyUp:
                    OnKeyUp?.Invoke(new KeyEvent { KeyCode = (int)evt.Key.Key, Repeat = evt.Key.Repeat });
                    break;

                case (uint)SDL.EventType.MouseMotion:
                    OnMouseMotion?.Invoke(new MouseMotionEvent
                    {
                        X = evt.Motion.X,
                        Y = evt.Motion.Y,
                        DeltaX = evt.Motion.XRel,
                        DeltaY = evt.Motion.YRel,
                    });
                    break;

                case (uint)SDL.EventType.MouseButtonDown:
                    OnMouseButtonDown?.Invoke(new MouseButtonEvent
                    {
                        Button = evt.Button.Button,
                        X = evt.Button.X,
                        Y = evt.Button.Y
                    });
                    break;

                case (uint)SDL.EventType.MouseButtonUp:
                    OnMouseButtonUp?.Invoke(new MouseButtonEvent
                    {
                        Button = evt.Button.Button,
                        X = evt.Button.X,
                        Y = evt.Button.Y
                    });
                    break;

                case (uint)SDL.EventType.WindowPixelSizeChanged:
                    Width = (uint)evt.Window.Data1;
                    Height = (uint)evt.Window.Data2;
                    OnResize?.Invoke(Width, Height);
                    break;
            }
        }
    }

    public void Present()
    {
    }

    public void Shutdown()
    {
        if (window == IntPtr.Zero) return;
        SDL.DestroyWindow(window);
        SDL.Quit();
        window = IntPtr.Zero;
    }

    public event Action<IKeyEvent>? OnKeyDown;
    public event Action<IKeyEvent>? OnKeyUp;
    public event Action<IMouseMotionEvent>? OnMouseMotion;
    public event Action<IMouseButtonEvent>? OnMouseButtonDown;
    public event Action<IMouseButtonEvent>? OnMouseButtonUp;
    public event Action<uint, uint>? OnResize;
    public event Action? OnClosing;

    public VkSurfaceKHR CreateSurface(VkInstance instance)
    {
        var success = SDL.VulkanCreateSurface(window, instance.Handle, IntPtr.Zero, out var surfaceHandle);

        if (!success) throw new InvalidOperationException($"Failed to create Vulkan surface: {SDL.GetError()}");

        return new VkSurfaceKHR((ulong)surfaceHandle);
    }

    public void GetRequiredExtensions(out string[] extensions)
    {
        extensions = SDL.VulkanGetInstanceExtensions(out _) ??
                     throw new InvalidOperationException("Failed to get Vulkan instance extensions");
    }

    public void CreateSurface(VkInstance instance, out VkSurfaceKHR surface)
    {
        var success = SDL.VulkanCreateSurface(window, instance.Handle, IntPtr.Zero, out var surfaceHandle);

        if (!success) throw new InvalidOperationException($"Failed to create Vulkan surface: {SDL.GetError()}");

        surface = new VkSurfaceKHR((ulong)surfaceHandle);
    }
}
