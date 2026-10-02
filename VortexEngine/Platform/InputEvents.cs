namespace VortexEngine.Platform;

public interface IKeyEvent
{
    int KeyCode { get; }
    bool Repeat { get; }
}

public interface IMouseMotionEvent
{
    float X { get; }
    float Y { get; }
    float DeltaX { get; }
    float DeltaY { get; }
}

public interface IMouseButtonEvent
{
    int Button { get; }
    float X { get; }
    float Y { get; }
}

public struct KeyEvent : IKeyEvent
{
    public int KeyCode { get; set; }
    public bool Repeat { get; set; }
}

public struct MouseMotionEvent : IMouseMotionEvent
{
    public float X { get; set; }
    public float Y { get; set; }
    public float DeltaX { get; set; }
    public float DeltaY { get; set; }
}

public struct MouseButtonEvent : IMouseButtonEvent
{
    public int Button { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
}
