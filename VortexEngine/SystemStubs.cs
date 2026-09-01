namespace VortexEngine;

public sealed class PhysicsSystem
{
    private readonly World world;

    public PhysicsSystem(World world)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public void Update(float dt)
    {
    }
}

public sealed class Renderer : IDisposable
{
    public void Update(float dt)
    {
    }

    public void Dispose()
    {
        
    }
}

public sealed class AssetManager
{
    public T LoadAsset<T>(string path) where T : class
    {
        throw new NotImplementedException();
    }
}

public sealed class AudioSystem : IDisposable
{
    public void Update(float dt)
    {
    }

    public void Dispose()
    {
    }
}