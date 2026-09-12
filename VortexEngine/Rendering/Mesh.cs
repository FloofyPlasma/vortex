using System.Numerics;
using System.Runtime.InteropServices;
using VortexEngine.Rendering.Vulkan;

namespace VortexEngine.Rendering;

[StructLayout(LayoutKind.Sequential)]
internal struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;
}

internal struct Mesh
{
    public List<Primitive> Primitives;
}