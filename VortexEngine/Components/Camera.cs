using System.Numerics;
using VortexEngine.Core;

namespace VortexEngine.Components;

public struct Camera : IComponent
{
    public float FieldOfView { get; set; }
    public float NearPlane { get; set; }
    public float FarPlane { get; set; }
    public bool IsActive { get; set; }

    public Camera()
    {
        FieldOfView = 45f;
        NearPlane = 0.1f;
        FarPlane = 1000f;
        IsActive = false;
    }
}
