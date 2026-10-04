using System.Numerics;
using VortexEngine.Core;

namespace VortexEngine.Components;

public struct Camera : IComponent
{
    public Camera()
    {
    }

    public Vector3 Position { get; set; } = default;
    public Vector3 Target { get; set; } = default;
    public Vector3 Up { get; set; } = default;
    public float FieldOfView { get; set; } = MathF.PI / 4.0f;
    public float Near { get; set; } = 0.1f;
    public float Far { get; set; } = 100.0f;
}
