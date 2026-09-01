using Avalonia;
using System;

namespace VortexEditor;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWaylandWithFallback()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
