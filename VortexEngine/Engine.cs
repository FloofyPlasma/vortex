using System;

namespace VortexEngine;

public sealed class Engine : IDisposable
{
    public World World { get; }
    public PhysicsSystem Physics { get; }
    public Renderer Renderer { get; }
    public AssetManager Assets { get; }
    public AudioSystem Audio { get; }

    public Engine()
    {
        World = new World();
        Physics = new PhysicsSystem(World);
        Renderer = new Renderer();
        Assets = new AssetManager();
        Audio = new AudioSystem();
    }

    public void Update(float dt)
    {
        Physics.Update(dt);
        Audio.Update(dt);
    }

    public void Dispose()
    {
        Renderer?.Dispose();
        Audio?.Dispose();
    }
}