using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace macViz;

public sealed class ControlPanelWindow : GameWindow
{
    private readonly MinimalGameWindow _appWindow;
    private ImGuiController? _imGuiController;
    private bool _initialized;

    public ControlPanelWindow(
        GameWindowSettings gameWindowSettings,
        NativeWindowSettings nativeWindowSettings,
        MinimalGameWindow appWindow)
        : base(gameWindowSettings, nativeWindowSettings)
    {
        _appWindow = appWindow;
    }

    public void RenderExternal(float deltaSeconds)
    {
        if (IsExiting)
        {
            return;
        }

        EnsureInitialized();

        MakeCurrent();
        _imGuiController!.Update(deltaSeconds);

        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        ImGui.NewFrame();
        _appWindow.DrawControlUi();
        ImGui.Render();
        _imGuiController.RenderDrawData(ImGui.GetDrawData());

        SwapBuffers();
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        MakeCurrent();
        GL.ClearColor(new Color4(0.08f, 0.08f, 0.1f, 1f));
        _imGuiController = new ImGuiController(ClientSize.X, ClientSize.Y, this);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        _initialized = true;
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        if (!_initialized)
        {
            return;
        }

        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        _imGuiController?.WindowResized(e.Width, e.Height);
    }

    protected override void OnUnload()
    {
        _imGuiController?.Dispose();
        base.OnUnload();
    }
}
