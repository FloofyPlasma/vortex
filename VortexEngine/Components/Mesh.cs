using VortexEngine.Core;

namespace VortexEngine.Components;

public struct Mesh : IComponent
{
    public string MeshPath { get; set; }
    public string MaterialPath { get; set; }

    public Mesh()
    {
        MeshPath = string.Empty;
        MaterialPath = string.Empty;
    }
}
