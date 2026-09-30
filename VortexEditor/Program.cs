using System.Diagnostics;
using System.Numerics;
using VortexEditor.Platform;
using VortexEngine;
using VortexEngine.Components;

namespace VortexEditor;

internal class Program
{
    private static void Main(string[] args)
    {
        var window = new SDLWindow(1920, 1080, "Vortex Editor");
        var engine = new Engine();

        try
        {
            engine.Initialize(window);

            var hdrData = File.ReadAllBytes("VortexEditor/Assets/Workshop.exr");
            var cubemapHandle = engine.Renderer.LoadEquirectangularHDRI(hdrData, 2048, 1024);

            var meshBytes = File.ReadAllBytes("VortexEditor/Assets/MetalRoughSpheres.glb");
            var meshHandle = engine.Renderer.LoadMesh(meshBytes);

            var meshEntity = engine.World.CreateEntity();
            engine.World.AddComponent(meshEntity, new Transform
            {
                Position = Vector3.Zero,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One
            });
            engine.World.AddComponent(meshEntity, new MeshRenderer { MeshHandle = meshHandle });

            var cameraEntity = engine.World.CreateEntity();
            engine.World.AddComponent(cameraEntity, new Camera
            {
                Position = new Vector3(0, 15, 10),
                Target = Vector3.Zero,
                Up = Vector3.UnitY,
            });

            var lightEntity = engine.World.CreateEntity();
            engine.World.AddComponent(lightEntity, new DirectionalLight
            {
                Direction = new Vector3(0, -2.5f, -3.5f),
                Color = new Vector3(0.8f, 0.8f, 0.8f),
                Intensity = 1.0f,
            });

            var clock = Stopwatch.StartNew();

            while (window.IsOpen)
            {
                window.ProcessEvents();

                var dt = (float)clock.Elapsed.TotalSeconds;
                clock.Restart();

                engine.Update(dt);
                engine.Render();

                window.Present();
            }
        }
        finally
        {
            window.Shutdown();
        }
    }
}