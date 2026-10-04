using VortexEngine.Core;
using VortexEngine.Rendering;

namespace VortexEngine.Components;

public struct MeshRenderer : IComponent
{
    public MeshHandle MeshHandle { get; set; }
}
