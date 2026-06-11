using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace macViz;

public class MinimalGameWindow : GameWindow
{
    private ImGuiController? _imGuiController;
    private AudioSpectrumAnalyzer? _audioSpectrum;
    private readonly float[] _latestSpectrum = new float[512];
    private readonly List<IVisual> _visuals = [];
    private int _selectedVisualIndex;
    private int _selectedParameterIndex;
    private bool _tabWasDown;
    private bool _leftWasDown;
    private bool _rightWasDown;
    private double _elapsedTime;
    private string? _audioInitError;
    private double _lastSpectrumUpdateTime;
    private double _lastAudioHealthLogTime;

    public MinimalGameWindow(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        GL.ClearColor(new Color4(0f, 0f, 0.2f, 1f));
        _imGuiController = new ImGuiController(ClientSize.X, ClientSize.Y, this);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);

        _visuals.Add(new SpectrumBars2d());

        try
        {
            _audioSpectrum = new AudioSpectrumAnalyzer();
        }
        catch (Exception ex)
        {
            _audioInitError = ex.Message;
            Console.WriteLine($"[Audio] Initialization failed: {_audioInitError}");
        }
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        _imGuiController!.Update((float)args.Time);
        _elapsedTime += args.Time;

        if (_audioSpectrum is not null && _audioSpectrum.TryDequeueLatest(out var latest))
        {
            var count = Math.Min(latest.Length, _latestSpectrum.Length);
            var totalDelta = 0f;

            for (var i = 0; i < count; i++)
            {
                totalDelta += MathF.Abs(latest[i] - _latestSpectrum[i]);
            }

            Array.Copy(latest, _latestSpectrum, count);

            // Only consider it "live" if spectrum is actually changing.
            if (totalDelta > 0.5f)
            {
                _lastSpectrumUpdateTime = _elapsedTime;
            }
        }

        if (_elapsedTime - _lastSpectrumUpdateTime > 0.25)
        {
            FillFallbackSpectrum((float)_elapsedTime, _latestSpectrum);
        }

        if (_audioSpectrum is not null && _elapsedTime - _lastAudioHealthLogTime > 2.0)
        {
            _lastAudioHealthLogTime = _elapsedTime;
            Console.WriteLine($"[Audio] Record callbacks: {_audioSpectrum.RecordCallbackCount}");
        }

        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        if (_visuals.Count > 0)
        {
            _visuals[_selectedVisualIndex].Render(_latestSpectrum, (float)_elapsedTime);
        }

        ImGui.NewFrame();

        ImGui.SetNextWindowPos(new System.Numerics.Vector2(12, 12), ImGuiCond.Once);
        ImGui.Begin("Parameters", ImGuiWindowFlags.AlwaysAutoResize);
        ImGui.Text($"FPS: {ImGui.GetIO().Framerate:F1}");

        var activeVisualName = _visuals.Count > 0 ? _visuals[_selectedVisualIndex].Name : "None";
        if (ImGui.BeginCombo("Visual", activeVisualName))
        {
            for (var i = 0; i < _visuals.Count; i++)
            {
                var selected = i == _selectedVisualIndex;
                if (ImGui.Selectable(_visuals[i].Name, selected))
                {
                    _selectedVisualIndex = i;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (_visuals.Count > 0)
        {
            var activeVisual = _visuals[_selectedVisualIndex];
            HandleParameterKeyboardNavigation(activeVisual);
            DrawParameters(activeVisual);
        }

        if (!string.IsNullOrWhiteSpace(_audioInitError))
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f), $"Audio init failed: {_audioInitError}");
        }

        ImGui.End();

        ImGui.Render();
        _imGuiController.RenderDrawData(ImGui.GetDrawData());

        SwapBuffers();
    }

    private void DrawParameters(IVisual visual)
    {
        for (var i = 0; i < visual.Parameters.Count; i++)
        {
            var parameter = visual.Parameters[i];
            var isSelected = i == _selectedParameterIndex;
            if (isSelected)
            {
                ImGui.PushStyleColor(ImGuiCol.FrameBg, new System.Numerics.Vector4(0.25f, 0.35f, 0.55f, 1f));
            }

            switch (parameter)
            {
                case Parameter<int> intParameter:
                {
                    var value = intParameter.Value;
                    if (ImGui.SliderInt(intParameter.Name, ref value, intParameter.Min, intParameter.Max))
                    {
                        intParameter.Value = value;
                    }

                    break;
                }
                case Parameter<float> floatParameter:
                {
                    var value = floatParameter.Value;
                    if (ImGui.SliderFloat(floatParameter.Name, ref value, floatParameter.Min, floatParameter.Max))
                    {
                        floatParameter.Value = value;
                    }

                    break;
                }
            }

            if (isSelected)
            {
                ImGui.PopStyleColor();
            }
        }

        ImGui.TextDisabled("Tab: next parameter | Shift+Tab: previous | Left/Right: adjust");
    }

    private void HandleParameterKeyboardNavigation(IVisual visual)
    {
        if (visual.Parameters.Count == 0)
        {
            return;
        }

        if (_selectedParameterIndex >= visual.Parameters.Count)
        {
            _selectedParameterIndex = 0;
        }

        var keyboard = KeyboardState;

        var tabDown = keyboard.IsKeyDown(Keys.Tab);
        var leftDown = keyboard.IsKeyDown(Keys.Left);
        var rightDown = keyboard.IsKeyDown(Keys.Right);

        var shiftDown = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);

        if (tabDown && !_tabWasDown)
        {
            var delta = shiftDown ? -1 : 1;
            _selectedParameterIndex = (_selectedParameterIndex + delta + visual.Parameters.Count) % visual.Parameters.Count;
        }

        if (leftDown && !_leftWasDown)
        {
            AdjustParameter(visual.Parameters[_selectedParameterIndex], -1, shiftDown);
        }

        if (rightDown && !_rightWasDown)
        {
            AdjustParameter(visual.Parameters[_selectedParameterIndex], 1, shiftDown);
        }

        _tabWasDown = tabDown;
        _leftWasDown = leftDown;
        _rightWasDown = rightDown;
    }

    private static void AdjustParameter(IParameter parameter, int direction, bool fast)
    {
        switch (parameter)
        {
            case Parameter<int> intParameter:
            {
                var step = fast ? 4 : 1;
                intParameter.Value = Math.Clamp(intParameter.Value + (direction * step), intParameter.Min, intParameter.Max);
                break;
            }
            case Parameter<float> floatParameter:
            {
                var range = floatParameter.Max - floatParameter.Min;
                var step = (fast ? 0.05f : 0.01f) * range;
                floatParameter.Value = Math.Clamp(floatParameter.Value + (direction * step), floatParameter.Min, floatParameter.Max);
                break;
            }
        }
    }

    private static void FillFallbackSpectrum(float time, float[] spectrum)
    {
        for (var i = 0; i < spectrum.Length; i++)
        {
            var t = time * 2.2f + (i * 0.04f);
            var wave = (MathF.Sin(t) * 0.5f + 0.5f) * MathF.Exp(-i / 140f);
            spectrum[i] = -95f + (wave * 85f);
        }
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        _imGuiController?.WindowResized(e.Width, e.Height);
    }

    protected override void OnUnload()
    {
        _audioSpectrum?.Dispose();
        foreach (var visual in _visuals)
        {
            visual.Dispose();
        }

        _imGuiController?.Dispose();
        base.OnUnload();
    }
}

public static class Program
{
    public static void Main()
    {
        var gameWindowSettings = GameWindowSettings.Default;
        var nativeWindowSettings = new NativeWindowSettings
        {
            ClientSize = new Vector2i(1280, 720),
            Title = "macViz"
        };

        using var window = new MinimalGameWindow(gameWindowSettings, nativeWindowSettings);
        window.Run();
    }
}
