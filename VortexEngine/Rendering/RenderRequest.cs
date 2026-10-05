using System.Numerics;
using VortexEngine.Components;

namespace VortexEngine.Rendering;

public sealed class RenderRequest
{
    public uint ViewportWidth { get; set; }
    public uint ViewportHeight { get; set; }
    public Camera Camera { get; set; }
    public IReadOnlyList<RenderMesh> Meshes { get; set; } = [];
    public DirectionalLight Light { get; set; }
}

public struct RenderMesh
{
    public MeshHandle Handle { get; set; }
    public Matrix4x4 Transform { get; set; }
}
