using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace macViz;

public partial class MinimalGameWindow : GameWindow
{
    private readonly object _stateLock = new();
    private ControlPanelWindow? _controlPanelWindow;
    private bool _focusControlWindowPending;
    private AudioSpectrumAnalyzer? _audioSpectrum;
    private readonly float[] _latestSpectrum = new float[512];
    private readonly List<IVisual> _visuals = [];
    private readonly LfoEngine _lfoEngine = new();
    private readonly TempoController _internalTempo = new();
    private readonly AbletonLinkManager _abletonLink = new();
    private bool _preferAbletonLink = true;
    private static readonly (string Label, float Multiplier)[] SyncSpeedOptions =
    [
        ("1:4", 0.25f),
        ("1:2", 0.5f),
        ("1:1", 1f),
        ("2:1", 2f),
        ("4:1", 4f)
    ];

    public const int DefaultOutputWidth = 1280;
    public const int DefaultOutputHeight = 720;
    public const int DefaultControlWidth = 1600;
    public const int DefaultControlHeight = 1200;
    public const string OutputWindowTitle = "macViz - Output";
    public const string ControlWindowTitle = "macViz - Controls";

    private int _selectedVisualIndex;
    private int _selectedParameterIndex;
    private readonly Dictionary<(IParameter Parameter, int LfoId), LfoModulation> _modulationMatrix = new();
    private readonly Dictionary<(IParameter Parameter, int FftId), AudioModulation> _audioModulationMatrix = new();
    private readonly List<FftModSource> _fftSources = [];
    private int _nextFftSourceId = 1;

    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private string _pipelinePresetFilePath = "pipeline-presets.json";
    private string _newPipelinePresetName = "Default";
    private int _selectedPipelinePresetIndex;
    private PipelinePresetBank _pipelinePresetBank = new();
    private string _pipelinePresetStatus = "No preset file loaded";

    private bool _tabWasDown;
    private bool _leftWasDown;
    private bool _rightWasDown;
    private bool _vWasDown;

    private double _elapsedTime;
    private string? _audioInitError;
    private double _lastSpectrumUpdateTime;
    private double _lastAudioHealthLogTime;

    public MinimalGameWindow(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
    }

    public bool IsRunning => !IsExiting;

    public void AttachControlPanel(ControlPanelWindow controlPanelWindow)
    {
        _controlPanelWindow = controlPanelWindow;
        _focusControlWindowPending = true;
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        GL.ClearColor(new Color4(0f, 0f, 0.2f, 1f));
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);

        _visuals.Add(new SpectrumBars2d());
        _visuals.Add(new RotatingCube3D());
        _visuals.Add(new RotatingParticleSystem3D());
        _visuals.Add(new CymaticSpirals3D());
        _visuals.Add(new DiffusionPainting2D());

        _visuals.Add(new CameraFilterGrayscale());
        _visuals.Add(new CameraFilterEdgeDetection());
        _visuals.Add(new CameraSnapshotPeakHold());
        _visuals.Add(new VisualPipeline());

        var pipelineIndex = _visuals.FindIndex(v => v is VisualPipeline);
        _selectedVisualIndex = pipelineIndex >= 0 ? pipelineIndex : Math.Max(0, _visuals.Count - 1);

        AddFftSource();
        TryLoadPipelinePresetBankFromDisk();

        _abletonLink.Initialize(_internalTempo.Bpm);

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

        var deltaTime = (float)args.Time;

        lock (_stateLock)
        {
            _elapsedTime += args.Time;

            _internalTempo.Update(deltaTime);
            _abletonLink.Update(deltaTime);
            _lfoEngine.Update(deltaTime, GetActiveBeatClock());

            UpdateSpectrumData();
            UpdateAudioModulationBins();
            PruneOrphanedParameterAssignments();
            ApplyLfoToParameters();
            HandleVisualHotkeys();

            GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            if (_visuals.Count > 0)
            {
                _visuals[_selectedVisualIndex].Render(_latestSpectrum, (float)_elapsedTime);
            }
        }

        _controlPanelWindow?.ProcessEvents(0.0);
        _controlPanelWindow?.RenderExternal(deltaTime);

        if (_focusControlWindowPending && _controlPanelWindow is not null)
        {
            _controlPanelWindow.Focus();
            _focusControlWindowPending = false;
        }

        MakeCurrent();
        SwapBuffers();
    }

    public void DrawControlUi()
    {
        lock (_stateLock)
        {
            DrawParametersWindow();
            DrawVisualParametersWindow();
            DrawTempoManagementWindow();
            DrawPipelineGraphWindow();
            DrawPipelinePresetsWindow();
        }
    }

    private void UpdateSpectrumData()
    {
        if (_audioSpectrum is not null && _audioSpectrum.TryDequeueLatest(out var latest))
        {
            var count = Math.Min(latest.Length, _latestSpectrum.Length);
            var totalDelta = 0f;

            for (var i = 0; i < count; i++)
            {
                totalDelta += MathF.Abs(latest[i] - _latestSpectrum[i]);
            }

            Array.Copy(latest, _latestSpectrum, count);

            if (totalDelta > 0.5f)
            {
                _lastSpectrumUpdateTime = _elapsedTime;
            }
        }

        if (_elapsedTime - _lastSpectrumUpdateTime > 0.25)
        {
            SpectrumMath.FillFallbackSpectrum((float)_elapsedTime, _latestSpectrum);
        }

        if (_audioSpectrum is not null && _elapsedTime - _lastAudioHealthLogTime > 2.0)
        {
            _lastAudioHealthLogTime = _elapsedTime;
            Console.WriteLine($"[Audio] Record callbacks: {_audioSpectrum.RecordCallbackCount}");
        }
    }

    private IBeatClock GetActiveBeatClock()
    {
        if (_preferAbletonLink && _abletonLink.IsAvailable && _abletonLink.IsEnabled)
        {
            return _abletonLink;
        }

        return _internalTempo;
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
    }

    protected override void OnUnload()
    {
        _audioSpectrum?.Dispose();
        _abletonLink.Dispose();
        foreach (var visual in _visuals)
        {
            visual.Dispose();
        }

        base.OnUnload();
    }
}
