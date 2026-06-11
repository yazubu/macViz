using ImGuiNET;
using ImPlotNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace macViz;

public class MinimalGameWindow : GameWindow
{
    private ImGuiController? _imGuiController;
    private AudioSpectrumAnalyzer? _audioSpectrum;
    private readonly float[] _latestSpectrum = new float[512];
    private readonly List<IVisual> _visuals = [];
    private int _selectedVisualIndex;
    private double _elapsedTime;
    private string? _audioInitError;
    private bool _showDebugWindow = false;
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
        GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);

        ImPlot.CreateContext();

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

        GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        if (_visuals.Count > 0)
        {
            _visuals[_selectedVisualIndex].Render(_latestSpectrum, (float)_elapsedTime);
        }

        ImGui.NewFrame();

        if (_showDebugWindow)
        {
            ImGui.SetNextWindowPos(System.Numerics.Vector2.Zero, ImGuiCond.Always);
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(ClientSize.X, ClientSize.Y), ImGuiCond.Always);
            ImGui.Begin("Visual Synth", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
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

            if (!string.IsNullOrWhiteSpace(_audioInitError))
            {
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f), $"Audio init failed: {_audioInitError}");
            }
            else if (ImPlot.BeginPlot("Spectrum (dB)"))
            {
                ImPlot.SetupAxes("Bin", "dB", ImPlotAxisFlags.None, ImPlotAxisFlags.AutoFit);
                ImPlot.SetupAxesLimits(0, _latestSpectrum.Length - 1, -120, 6, ImPlotCond.Always);
                ImPlot.PlotLine("Magnitude", ref _latestSpectrum[0], _latestSpectrum.Length);
                ImPlot.EndPlot();
            }

            ImGui.End();
        }

        ImGui.Render();
        _imGuiController.RenderDrawData(ImGui.GetDrawData());

        SwapBuffers();
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
        GL.Viewport(0, 0, e.Width, e.Height);
        _imGuiController?.WindowResized(e.Width, e.Height);
    }

    protected override void OnUnload()
    {
        _audioSpectrum?.Dispose();
        foreach (var visual in _visuals)
        {
            visual.Dispose();
        }

        ImPlot.DestroyContext();
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
