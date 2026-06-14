using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;

namespace macViz;

public static class Program
{
    public static int Main()
    {
        try
        {
            var gameWindowSettings = new GameWindowSettings
            {
                UpdateFrequency = 60.0
            };

            var outputWindowSettings = new NativeWindowSettings
            {
                ClientSize = new Vector2i(MinimalGameWindow.DefaultOutputWidth, MinimalGameWindow.DefaultOutputHeight),
                Title = MinimalGameWindow.OutputWindowTitle
            };

            var controlSettings = new NativeWindowSettings
            {
                ClientSize = new Vector2i(MinimalGameWindow.DefaultControlWidth, MinimalGameWindow.DefaultControlHeight),
                Title = MinimalGameWindow.ControlWindowTitle
            };

            using var outputWindow = new MinimalGameWindow(gameWindowSettings, outputWindowSettings);
            using var controlWindow = new ControlPanelWindow(gameWindowSettings, controlSettings, outputWindow);
            outputWindow.AttachControlPanel(controlWindow);

            outputWindow.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Fatal] Application startup failed: {ex}");
            return 1;
        }
    }
}
