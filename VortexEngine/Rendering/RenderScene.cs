namespace VortexEngine.Rendering;

public sealed class RenderScene
{
    public IReadOnlyList<object> Drawables { get; set; } = new List<object>();
    public IReadOnlyList<object> Lights { get; set; } = new List<object>();
    public IReadOnlyList<object> Cameras { get; set; } = new List<object>();
}
