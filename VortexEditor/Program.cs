using System.Diagnostics;
using VortexEditor.Platform;
using VortexEngine;
using VortexEngine.Rendering;

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

            var meshBytes = File.ReadAllBytes("VortexEditor/Assets/DamagedHelmet.glb");
            var meshHandle = engine.Renderer.LoadMesh(meshBytes);

            var clock = Stopwatch.StartNew();

            while (window.IsOpen)
            {
                window.ProcessEvents();

                var dt = (float)clock.Elapsed.TotalSeconds;
                clock.Restart();

                engine.Update(dt);
                var scene = engine.ExtractRenderScene();
                engine.Renderer.Render(new RenderRequest());

                window.Present();
            }
        }
        finally
        {
            window.Shutdown();
        }
    }
}