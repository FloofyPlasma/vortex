using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace VortexEngine.Rendering;

[StructLayout(LayoutKind.Sequential)]
internal struct Vertex
{
    public Vector3 Position;
}

internal struct Mesh
{
    public VkBuffer VertexBuffer;
    public VmaAllocation VertexAllocation;
    public VkBuffer IndexBuffer;
    public VmaAllocation IndexAllocation;
    public uint IndexCount;
}