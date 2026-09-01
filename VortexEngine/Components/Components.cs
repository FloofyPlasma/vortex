using System.Numerics;

namespace VortexEngine.Components;

public struct Transform : IComponent
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public Transform()
    {
        Position = Vector3.Zero;
        Rotation = Quaternion.Identity;
        Scale = Vector3.One;
    }

    public Matrix4x4 GetMatrix()
    {
        return Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) *
               Matrix4x4.CreateTranslation(Position);
    }
}

public struct Mesh : IComponent
{
    public string MeshPath;
    public string MaterialPath;

    public Mesh(string meshPath, string materialPath)
    {
        MeshPath = meshPath;
        MaterialPath = materialPath;
    }
}

public struct RigidBody : IComponent
{
    public float Mass;
    public Vector3 LinearVelocity;
    public Vector3 AngularVelocity;

    public RigidBody(float mass = 1.0f)
    {
        Mass = mass;
        LinearVelocity = Vector3.Zero;
        AngularVelocity = Vector3.Zero;
    }
}

public struct Camera : IComponent
{
    public float FieldOfView;
    public float NearPlane;
    public float FarPlane;
    public bool IsActive;

    public Camera(float fov = 60f, float near = 0.1f, float far = 1000f)
    {
        FieldOfView = fov;
        NearPlane = near;
        FarPlane = far;
        IsActive = true;
    }
}