namespace VortexEngine.Platform;

public interface IPlatformWindow
{
    nint NativeHandle { get; }
    uint Width { get; }
    uint Height { get; }
    bool IsOpen { get; }

    void Present();
    void Shutdown();

    IInputState GetInputState();

    event Action<uint, uint> OnResize;
    event Action OnClosing;
}
