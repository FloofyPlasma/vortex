namespace VortexEngine.Platform;

public interface IInputState
{
    bool IsKeyPressed(int key);
    bool IsMouseButtonPressed(int button);
    int MouseX { get; }
    int MouseY { get; }
}
