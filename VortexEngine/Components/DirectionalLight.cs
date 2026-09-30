using System.Numerics;
using VortexEngine.Core;

namespace VortexEngine.Components;

public struct DirectionalLight : IComponent
{
    public Vector3 Direction { get; set; }
    public Vector3 Color { get; set; }
    public float Intensity { get; set; }
}