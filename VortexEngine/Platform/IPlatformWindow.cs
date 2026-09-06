namespace VortexEngine.Platform;

public interface IPlatformWindow
{
    nint NativeHandle { get; }
    uint Width { get; }
    uint Height { get; }
    bool IsOpen { get; }

    void Present();
    void Shutdown();
    void ProcessEvents();

    event Action<IKeyEvent>? OnKeyDown;
    event Action<IKeyEvent>? OnKeyUp;
    event Action<IMouseMotionEvent>? OnMouseMotion;
    event Action<IMouseButtonEvent>? OnMouseButtonDown;
    event Action<IMouseButtonEvent>? OnMouseButtonUp;
    event Action<uint, uint>? OnResize;
    event Action? OnClosing;
}