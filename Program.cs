using System.Text.Json;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace macViz;

public class MinimalGameWindow : GameWindow
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

    private enum ModulationInteractionMode
    {
        Instead,
        Add,
        Subtract,
        Multiply
    }

    private sealed class LfoModulation
    {
        public float Scale = 1f;
        public float Offset;
    }

    private sealed class AudioModulation
    {
        public float Scale = 1f;
        public float Offset;
        public int AudioBinIndex;
    }

    private sealed class FftModSource
    {
        public int Id;
        public int BinCount = 8;
        public float Smoothing = 0.75f;
        public float[] SmoothedBins = new float[8];
    }

    private sealed class PipelinePresetBank
    {
        public List<PipelinePresetEntry> Presets { get; set; } = [];
    }

    private sealed class PipelinePresetEntry
    {
        public string Name { get; set; } = "Preset";
        public VisualPipelinePresetState Pipeline { get; set; } = new();
        public List<LfoStateDto> Lfos { get; set; } = [];
        public List<FftSourceStateDto> FftSources { get; set; } = [];
        public List<ParameterModStateDto> ParameterModulations { get; set; } = [];
    }

    private sealed class LfoStateDto
    {
        public int SourceId { get; set; }
        public LfoWaveType WaveType { get; set; } = LfoWaveType.Sine;
        public float Frequency { get; set; } = 1f;
        public float Phase { get; set; }
        public float DutyCycle { get; set; } = 0.5f;
        public bool SyncEnabled { get; set; }
        public float SyncSpeedMultiplier { get; set; } = 1f;
    }

    private sealed class FftSourceStateDto
    {
        public int SourceId { get; set; }
        public int BinCount { get; set; } = 8;
        public float Smoothing { get; set; } = 0.75f;
    }

    private sealed class LfoAssignmentDto
    {
        public int SourceId { get; set; }
        public float Scale { get; set; } = 1f;
        public float Offset { get; set; }
    }

    private sealed class FftAssignmentDto
    {
        public int SourceId { get; set; }
        public int BinIndex { get; set; }
        public float Scale { get; set; } = 1f;
        public float Offset { get; set; }
    }

    private sealed class ParameterModStateDto
    {
        public int ParameterIndex { get; set; }
        public string ParameterName { get; set; } = string.Empty;
        public ModulationInteractionMode InteractionMode { get; set; } = ModulationInteractionMode.Add;
        public List<LfoAssignmentDto> LfoAssignments { get; set; } = [];
        public List<FftAssignmentDto> FftAssignments { get; set; } = [];
    }

    private int _selectedVisualIndex;
    private int _selectedParameterIndex;
    private readonly Dictionary<(IParameter Parameter, int LfoId), LfoModulation> _modulationMatrix = new();
    private readonly Dictionary<(IParameter Parameter, int FftId), AudioModulation> _audioModulationMatrix = new();
    private readonly Dictionary<IParameter, ModulationInteractionMode> _lfoFftInteractionModes = new();
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

        _visuals.Add(new CameraFilterGrayscale());
        _visuals.Add(new CameraFilterEdgeDetection());
        _visuals.Add(new CameraSnapshotPeakHold());
        _visuals.Add(new VisualPipeline());

        _selectedVisualIndex = Math.Min(1, _visuals.Count - 1); // Start on cube if available.

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
            DrawSettingsWindow();
            DrawLfoManagerWindow();
            DrawModMatrixWindow();
            DrawPipelineWindow();
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
            FillFallbackSpectrum((float)_elapsedTime, _latestSpectrum);
        }

        if (_audioSpectrum is not null && _elapsedTime - _lastAudioHealthLogTime > 2.0)
        {
            _lastAudioHealthLogTime = _elapsedTime;
            Console.WriteLine($"[Audio] Record callbacks: {_audioSpectrum.RecordCallbackCount}");
        }
    }

    private void DrawParametersWindow()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(12, 12), ImGuiCond.FirstUseEver);
        ImGui.Begin("Visual", ImGuiWindowFlags.AlwaysAutoResize);
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
                    _selectedParameterIndex = 0;
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

        ImGui.End();
    }

    private void DrawVisualParametersWindow()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(320, 12), ImGuiCond.FirstUseEver);
        ImGui.Begin("Parameters", ImGuiWindowFlags.AlwaysAutoResize);

        if (_visuals.Count > 0)
        {
            var activeVisual = _visuals[_selectedVisualIndex];
            HandleParameterKeyboardNavigation(activeVisual);
            DrawParameters(activeVisual);
        }

        ImGui.End();
    }

    private void DrawTempoManagementWindow()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(12, 100), ImGuiCond.FirstUseEver);
        ImGui.Begin("Tempo Management", ImGuiWindowFlags.AlwaysAutoResize);

        var useLink = _preferAbletonLink;
        var linkReady = _abletonLink.IsAvailable;
        if (!linkReady)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Checkbox("Use Ableton Link", ref useLink))
        {
            _preferAbletonLink = useLink;
            _abletonLink.Enable(_preferAbletonLink);
        }

        if (!linkReady)
        {
            ImGui.EndDisabled();
            ImGui.TextDisabled("Ableton Link unavailable (using internal tempo)");
        }

        var activeClock = GetActiveBeatClock();

        var bpm = activeClock.Bpm;
        ImGui.SetNextItemWidth(130);
        if (ImGui.SliderFloat("Tempo (BPM)", ref bpm, 60f, 160f, "%.1f"))
        {
            if (_preferAbletonLink && _abletonLink.IsAvailable)
            {
                _abletonLink.SetTempo(bpm);
            }
            else
            {
                _internalTempo.Bpm = bpm;
            }
        }

        var beatLight = activeClock.BeatPhase < 0.18f
            ? new System.Numerics.Vector4(0.2f, 1f, 0.2f, 1f)
            : new System.Numerics.Vector4(0.1f, 0.25f, 0.1f, 1f);
        ImGui.ColorButton("##metronome", beatLight, ImGuiColorEditFlags.NoTooltip, new System.Numerics.Vector2(18, 18));
        ImGui.SameLine();
        ImGui.Text($"Beat {(activeClock.BeatNumber % 4) + 1}");

        ImGui.Text($"Link BPM: {_abletonLink.Bpm:F2} | Peers: {_abletonLink.NumPeers}");
        ImGui.End();
    }

    private void DrawSettingsWindow()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(12, 250), ImGuiCond.FirstUseEver);
        ImGui.Begin("Settings", ImGuiWindowFlags.AlwaysAutoResize);

        if (_visuals.Count > 0)
        {
            var activeVisual = _visuals[_selectedVisualIndex];
            if (activeVisual is ICameraVisual cameraVisual)
            {
                DrawCameraControls(cameraVisual);
            }
            else
            {
                ImGui.TextDisabled("No camera settings for current visual.");
            }
        }

        ImGui.End();
    }

    private void DrawModMatrixWindow()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(320, 360), ImGuiCond.FirstUseEver);
        ImGui.Begin("Mod Matrix", ImGuiWindowFlags.AlwaysAutoResize);

        if (_visuals.Count > 0)
        {
            var activeVisual = _visuals[_selectedVisualIndex];
            DrawAudioModulationControls();
            DrawLfoAssignmentMatrix(activeVisual);
        }

        ImGui.End();
    }

    private void DrawPipelineWindow()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(1220, 12), ImGuiCond.FirstUseEver);
        ImGui.Begin("Pipeline", ImGuiWindowFlags.AlwaysAutoResize);

        if (_visuals.Count > 0)
        {
            var activeVisual = _visuals[_selectedVisualIndex];
            if (activeVisual is IVisualEditorPanel visualEditorPanel)
            {
                visualEditorPanel.DrawEditorPanel();
            }

            if (activeVisual is VisualPipeline visualPipeline)
            {
                DrawPipelinePresetManager(visualPipeline);
            }
            else
            {
                ImGui.TextDisabled("Select 'Visual Pipeline' visual to edit pipeline stages/presets.");
            }
        }

        ImGui.End();
    }

    private void DrawLfoManagerWindow()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(12, 460), ImGuiCond.FirstUseEver);
        ImGui.Begin("LFO Manager", ImGuiWindowFlags.AlwaysAutoResize);
        ImGui.PushItemWidth(170);

        if (ImGui.Button("Add LFO"))
        {
            _lfoEngine.AddLfo();
        }

        ImGui.SameLine();
        ImGui.Text($"Count: {_lfoEngine.Lfos.Count}");

        if (ImGui.Button("Add FFT Source"))
        {
            AddFftSource();
        }

        ImGui.SameLine();
        ImGui.Text($"FFT Sources: {_fftSources.Count}");

        for (var i = 0; i < _fftSources.Count; i++)
        {
            var source = _fftSources[i];
            ImGui.PushID($"fft_src_mgr_{source.Id}");
            if (ImGui.TreeNode($"FFT Source {source.Id}"))
            {
                var bins = source.BinCount;
                if (ImGui.SliderInt("Bins", ref bins, 1, 64))
                {
                    source.BinCount = bins;
                    Array.Resize(ref source.SmoothedBins, source.BinCount);
                    SanitizeAudioBinAssignments();
                }

                var smoothing = source.Smoothing;
                if (ImGui.SliderFloat("Smoothing", ref smoothing, 0f, 0.99f, "%.2f"))
                {
                    source.Smoothing = Math.Clamp(smoothing, 0f, 0.99f);
                }

                if (ImGui.Button("Remove FFT Source"))
                {
                    var removeId = source.Id;
                    ImGui.TreePop();
                    ImGui.PopID();
                    RemoveFftSourceAndAssignments(removeId);
                    break;
                }

                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        for (var i = 0; i < _lfoEngine.Lfos.Count; i++)
        {
            var lfo = _lfoEngine.Lfos[i];
            ImGui.PushID(lfo.Id);

            if (ImGui.TreeNode($"LFO {lfo.Id}"))
            {
                var wave = lfo.WaveType;
                if (ImGui.BeginCombo("Wave", wave.ToString()))
                {
                    foreach (LfoWaveType candidate in Enum.GetValues<LfoWaveType>())
                    {
                        var selected = candidate == wave;
                        if (ImGui.Selectable(candidate.ToString(), selected))
                        {
                            lfo.WaveType = candidate;
                        }

                        if (selected)
                        {
                            ImGui.SetItemDefaultFocus();
                        }
                    }

                    ImGui.EndCombo();
                }

                var syncEnabled = lfo.SyncEnabled;
                if (ImGui.Checkbox("Sync to Beat", ref syncEnabled))
                {
                    lfo.SyncEnabled = syncEnabled;
                }

                if (lfo.SyncEnabled)
                {
                    var currentLabel = GetSyncSpeedLabel(lfo.SyncSpeedMultiplier);
                    if (ImGui.BeginCombo("Speed", currentLabel))
                    {
                        foreach (var option in SyncSpeedOptions)
                        {
                            var selected = MathF.Abs(lfo.SyncSpeedMultiplier - option.Multiplier) < 0.0001f;
                            if (ImGui.Selectable(option.Label, selected))
                            {
                                lfo.SyncSpeedMultiplier = option.Multiplier;
                            }

                            if (selected)
                            {
                                ImGui.SetItemDefaultFocus();
                            }
                        }

                        ImGui.EndCombo();
                    }
                }
                else
                {
                    var freq = lfo.Frequency;
                    if (ImGui.SliderFloat("Frequency (Hz)", ref freq, 0.01f, 20f, "%.2f"))
                    {
                        lfo.Frequency = freq;
                    }
                }

                if (lfo.WaveType == LfoWaveType.PWM)
                {
                    var duty = lfo.DutyCycle;
                    if (ImGui.SliderFloat("Duty", ref duty, 0.05f, 0.95f, "%.2f"))
                    {
                        lfo.DutyCycle = duty;
                    }
                }

                ImGui.Text($"Output: {lfo.Output:F3}");
                ImGui.ProgressBar((lfo.Output + 1f) * 0.5f, new System.Numerics.Vector2(220, 0), "");

                if (ImGui.Button("Remove"))
                {
                    var id = lfo.Id;
                    ImGui.TreePop();
                    ImGui.PopID();
                    RemoveLfoAndAssignments(id);
                    break;
                }

                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        ImGui.PopItemWidth();
        ImGui.End();
    }

    private IBeatClock GetActiveBeatClock()
    {
        if (_preferAbletonLink && _abletonLink.IsAvailable && _abletonLink.IsEnabled)
        {
            return _abletonLink;
        }

        return _internalTempo;
    }

    private static string GetSyncSpeedLabel(float multiplier)
    {
        foreach (var option in SyncSpeedOptions)
        {
            if (MathF.Abs(option.Multiplier - multiplier) < 0.0001f)
            {
                return option.Label;
            }
        }

        return $"x{multiplier:0.##}";
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
                    ImGui.SetNextItemWidth(220);
                    if (ImGui.SliderInt(intParameter.Name, ref value, intParameter.Min, intParameter.Max))
                    {
                        intParameter.Value = value;
                    }

                    ImGui.SameLine();
                    ImGui.TextDisabled($"= {intParameter.CurrentValue}");
                    break;
                }
                case Parameter<float> floatParameter:
                {
                    var value = floatParameter.Value;
                    ImGui.SetNextItemWidth(220);
                    if (ImGui.SliderFloat(floatParameter.Name, ref value, floatParameter.Min, floatParameter.Max))
                    {
                        floatParameter.Value = value;
                    }

                    ImGui.SameLine();
                    ImGui.TextDisabled($"= {floatParameter.CurrentValue:F2}");
                    break;
                }
            }

            if (isSelected)
            {
                ImGui.PopStyleColor();
            }

            ImGui.Separator();
        }

        ImGui.TextDisabled("Tab: next parameter | Shift+Tab: previous | Left/Right: adjust base value");
    }

    private static void DrawCameraControls(ICameraVisual cameraVisual)
    {
        ImGui.Separator();
        ImGui.Text("Camera");

        if (ImGui.Button("Refresh Devices"))
        {
            cameraVisual.RefreshDevices();
        }

        var selectedLabel = $"Device {cameraVisual.SelectedDeviceIndex}";
        if (cameraVisual.AvailableDeviceIndices.Count == 0)
        {
            ImGui.TextDisabled("No devices found");
        }
        else if (ImGui.BeginCombo("Camera Device", selectedLabel))
        {
            foreach (var deviceIndex in cameraVisual.AvailableDeviceIndices)
            {
                var selected = deviceIndex == cameraVisual.SelectedDeviceIndex;
                if (ImGui.Selectable($"Device {deviceIndex}", selected))
                {
                    cameraVisual.SetSelectedDeviceIndex(deviceIndex);
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(cameraVisual.CameraStatus);
    }

    private void DrawAudioModulationControls()
    {
        ImGui.Separator();
        ImGui.Text("Audio Modulation (FFT)");
        ImGui.TextDisabled("Each FFT source has independent bin count and smoothing.");

        if (_fftSources.Count == 0)
        {
            ImGui.TextDisabled("Add FFT Source in LFO Manager.");
            return;
        }

        for (var i = 0; i < _fftSources.Count; i++)
        {
            var source = _fftSources[i];
            ImGui.PushID($"fft_preview_src_{source.Id}");

            if (ImGui.TreeNode($"FFT Source {source.Id} Preview"))
            {
                ImGui.TextDisabled($"Bins: {source.BinCount} | Smoothing: {source.Smoothing:F2}");
                for (var bin = 0; bin < source.SmoothedBins.Length; bin++)
                {
                    var value = source.SmoothedBins[bin];
                    ImGui.ProgressBar(value, new System.Numerics.Vector2(140, 0), $"Bin {bin + 1}: {value:F2}");
                }

                ImGui.TreePop();
            }

            ImGui.PopID();
        }
    }

    private void DrawLfoAssignmentMatrix(IVisual visual)
    {
        ImGui.Text("Mod Matrix");
        ImGui.TextDisabled("Assigned source output multiplies the base parameter value.");

        var columns = 2 + _fftSources.Count + _lfoEngine.Lfos.Count;
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX;
        if (!ImGui.BeginTable("LfoModMatrix", columns, flags, new System.Numerics.Vector2(1100, 320)))
        {
            return;
        }

        ImGui.TableSetupColumn("Parameter", ImGuiTableColumnFlags.WidthFixed, 260);
        ImGui.TableSetupColumn("LFO↔FFT", ImGuiTableColumnFlags.WidthFixed, 120);

        foreach (var fft in _fftSources)
        {
            ImGui.TableSetupColumn($"FFT {fft.Id}", ImGuiTableColumnFlags.WidthFixed, 120);
        }

        foreach (var lfo in _lfoEngine.Lfos)
        {
            ImGui.TableSetupColumn($"LFO {lfo.Id}", ImGuiTableColumnFlags.WidthFixed, 120);
        }

        ImGui.TableHeadersRow();

        for (var row = 0; row < visual.Parameters.Count; row++)
        {
            var parameter = visual.Parameters[row];

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text(parameter.Name);

            ImGui.TableSetColumnIndex(1);
            ImGui.PushID($"mode_{row}");
            var interactionMode = _lfoFftInteractionModes.GetValueOrDefault(parameter, ModulationInteractionMode.Add);
            if (ImGui.BeginCombo("##interaction", interactionMode.ToString()))
            {
                foreach (ModulationInteractionMode candidate in Enum.GetValues<ModulationInteractionMode>())
                {
                    var isSelected = candidate == interactionMode;
                    if (ImGui.Selectable(candidate.ToString(), isSelected))
                    {
                        _lfoFftInteractionModes[parameter] = candidate;
                        interactionMode = candidate;
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.PopID();

            for (var fftCol = 0; fftCol < _fftSources.Count; fftCol++)
            {
                var fft = _fftSources[fftCol];
                var audioKey = (parameter, fft.Id);
                ImGui.TableSetColumnIndex(fftCol + 2);
                ImGui.PushID($"fft_cell_{row}_{fft.Id}");

                var fftAssigned = _audioModulationMatrix.TryGetValue(audioKey, out var audioMod);
                if (ImGui.Checkbox("##assign", ref fftAssigned))
                {
                    if (fftAssigned)
                    {
                        _audioModulationMatrix[audioKey] = audioMod ?? new AudioModulation();
                    }
                    else
                    {
                        _audioModulationMatrix.Remove(audioKey);
                    }
                }

                if (fftAssigned)
                {
                    audioMod ??= new AudioModulation();
                    _audioModulationMatrix[audioKey] = audioMod;

                    if (audioMod.AudioBinIndex >= fft.BinCount)
                    {
                        audioMod.AudioBinIndex = Math.Max(0, fft.BinCount - 1);
                    }

                    var currentBinLabel = $"Bin {audioMod.AudioBinIndex + 1}";
                    if (ImGui.BeginCombo("##fftBin", currentBinLabel))
                    {
                        for (var bin = 0; bin < fft.BinCount; bin++)
                        {
                            var isSelected = bin == audioMod.AudioBinIndex;
                            if (ImGui.Selectable($"Bin {bin + 1}", isSelected))
                            {
                                audioMod.AudioBinIndex = bin;
                            }

                            if (isSelected)
                            {
                                ImGui.SetItemDefaultFocus();
                            }
                        }

                        ImGui.EndCombo();
                    }

                    var audioScale = audioMod.Scale;
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderFloat("##fftScale", ref audioScale, 0f, 2f, "Scale %.2f"))
                    {
                        audioMod.Scale = audioScale;
                    }

                    var audioOffset = audioMod.Offset;
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderFloat("##fftOffset", ref audioOffset, -1f, 1f, "Offset %.2f"))
                    {
                        audioMod.Offset = audioOffset;
                    }

                    var binValue = GetAudioBinValue(fft.Id, audioMod.AudioBinIndex);
                    var binModulated = (binValue * audioMod.Scale) + audioMod.Offset;
                    ImGui.TextDisabled($"BIN: {binValue:F2} → {binModulated:F2}");
                }

                ImGui.PopID();
            }

            for (var lfoCol = 0; lfoCol < _lfoEngine.Lfos.Count; lfoCol++)
            {
                var lfo = _lfoEngine.Lfos[lfoCol];
                var key = (parameter, lfo.Id);

                ImGui.TableSetColumnIndex(lfoCol + 2 + _fftSources.Count);
                ImGui.PushID($"cell_{row}_{lfo.Id}");

                var assigned = _modulationMatrix.TryGetValue(key, out var modulation);
                if (ImGui.Checkbox("##assign", ref assigned))
                {
                    if (assigned)
                    {
                        _modulationMatrix[key] = modulation ?? new LfoModulation();
                    }
                    else
                    {
                        _modulationMatrix.Remove(key);
                    }
                }

                if (assigned)
                {
                    modulation ??= new LfoModulation();
                    _modulationMatrix[key] = modulation;

                    var scale = modulation.Scale;
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderFloat("##scale", ref scale, 0f, 2f, "Scale %.2f"))
                    {
                        modulation.Scale = scale;
                    }

                    var offset = modulation.Offset;
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderFloat("##offset", ref offset, -1f, 1f, "Offset %.2f"))
                    {
                        modulation.Offset = offset;
                    }

                    var lfoValue = lfo.Output;
                    var lfoModulated = (lfoValue * modulation.Scale) + modulation.Offset;
                    ImGui.TextDisabled($"LFO: {lfoValue:F2} → {lfoModulated:F2}");
                }

                ImGui.PopID();
            }
        }

        ImGui.EndTable();
    }

    private void DrawPipelinePresetManager(VisualPipeline visualPipeline)
    {
        ImGui.Separator();
        ImGui.Text("Pipeline Presets");

        ImGui.SetNextItemWidth(420);
        ImGui.InputText("Preset File", ref _pipelinePresetFilePath, 512);
        if (ImGui.Button("Load Preset File"))
        {
            TryLoadPipelinePresetBankFromDisk();
        }

        ImGui.SameLine();
        if (ImGui.Button("Save Preset File"))
        {
            TrySavePipelinePresetBankToDisk();
        }

        ImGui.TextDisabled(_pipelinePresetStatus);

        if (_pipelinePresetBank.Presets.Count > 0)
        {
            _selectedPipelinePresetIndex = Math.Clamp(_selectedPipelinePresetIndex, 0, _pipelinePresetBank.Presets.Count - 1);
            var selectedName = _pipelinePresetBank.Presets[_selectedPipelinePresetIndex].Name;
            if (ImGui.BeginCombo("Preset", selectedName))
            {
                for (var i = 0; i < _pipelinePresetBank.Presets.Count; i++)
                {
                    var selected = i == _selectedPipelinePresetIndex;
                    if (ImGui.Selectable(_pipelinePresetBank.Presets[i].Name, selected))
                    {
                        _selectedPipelinePresetIndex = i;
                        _newPipelinePresetName = _pipelinePresetBank.Presets[i].Name;
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            if (ImGui.Button("Apply Selected"))
            {
                ApplyPipelinePreset(_pipelinePresetBank.Presets[_selectedPipelinePresetIndex], visualPipeline);
            }

            ImGui.SameLine();
            if (ImGui.Button("Update Selected"))
            {
                var selectedPreset = _pipelinePresetBank.Presets[_selectedPipelinePresetIndex];
                var replacement = CapturePipelinePreset(selectedPreset.Name, visualPipeline);
                _pipelinePresetBank.Presets[_selectedPipelinePresetIndex] = replacement;
                _pipelinePresetStatus = $"Updated preset '{replacement.Name}'.";
            }

            ImGui.SameLine();
            if (ImGui.Button("Delete Selected"))
            {
                var removedName = _pipelinePresetBank.Presets[_selectedPipelinePresetIndex].Name;
                _pipelinePresetBank.Presets.RemoveAt(_selectedPipelinePresetIndex);
                if (_pipelinePresetBank.Presets.Count == 0)
                {
                    _selectedPipelinePresetIndex = 0;
                }
                else
                {
                    _selectedPipelinePresetIndex = Math.Clamp(_selectedPipelinePresetIndex, 0, _pipelinePresetBank.Presets.Count - 1);
                }

                _pipelinePresetStatus = $"Deleted preset '{removedName}'.";
            }
        }
        else
        {
            ImGui.TextDisabled("No presets in file.");
        }

        ImGui.SetNextItemWidth(320);
        ImGui.InputText("Preset Name", ref _newPipelinePresetName, 128);
        if (ImGui.Button("Capture As New Preset"))
        {
            var name = string.IsNullOrWhiteSpace(_newPipelinePresetName)
                ? $"Preset {_pipelinePresetBank.Presets.Count + 1}"
                : _newPipelinePresetName.Trim();

            var newPreset = CapturePipelinePreset(name, visualPipeline);
            _pipelinePresetBank.Presets.Add(newPreset);
            _selectedPipelinePresetIndex = _pipelinePresetBank.Presets.Count - 1;
            _pipelinePresetStatus = $"Captured preset '{name}'.";
        }
    }

    private PipelinePresetEntry CapturePipelinePreset(string presetName, VisualPipeline visualPipeline)
    {
        var entry = new PipelinePresetEntry
        {
            Name = presetName,
            Pipeline = visualPipeline.CapturePresetState()
        };

        foreach (var lfo in _lfoEngine.Lfos)
        {
            entry.Lfos.Add(new LfoStateDto
            {
                SourceId = lfo.Id,
                WaveType = lfo.WaveType,
                Frequency = lfo.Frequency,
                Phase = lfo.Phase,
                DutyCycle = lfo.DutyCycle,
                SyncEnabled = lfo.SyncEnabled,
                SyncSpeedMultiplier = lfo.SyncSpeedMultiplier
            });
        }

        foreach (var fft in _fftSources)
        {
            entry.FftSources.Add(new FftSourceStateDto
            {
                SourceId = fft.Id,
                BinCount = fft.BinCount,
                Smoothing = fft.Smoothing
            });
        }

        for (var i = 0; i < visualPipeline.Parameters.Count; i++)
        {
            var parameter = visualPipeline.Parameters[i];
            var hasMode = _lfoFftInteractionModes.TryGetValue(parameter, out var interactionMode);

            var paramState = new ParameterModStateDto
            {
                ParameterIndex = i,
                ParameterName = parameter.Name,
                InteractionMode = hasMode ? interactionMode : ModulationInteractionMode.Add
            };

            foreach (var lfo in _lfoEngine.Lfos)
            {
                var key = (parameter, lfo.Id);
                if (_modulationMatrix.TryGetValue(key, out var modulation))
                {
                    paramState.LfoAssignments.Add(new LfoAssignmentDto
                    {
                        SourceId = lfo.Id,
                        Scale = modulation.Scale,
                        Offset = modulation.Offset
                    });
                }
            }

            foreach (var fft in _fftSources)
            {
                var key = (parameter, fft.Id);
                if (_audioModulationMatrix.TryGetValue(key, out var audioMod))
                {
                    paramState.FftAssignments.Add(new FftAssignmentDto
                    {
                        SourceId = fft.Id,
                        BinIndex = audioMod.AudioBinIndex,
                        Scale = audioMod.Scale,
                        Offset = audioMod.Offset
                    });
                }
            }

            if (hasMode || paramState.LfoAssignments.Count > 0 || paramState.FftAssignments.Count > 0)
            {
                entry.ParameterModulations.Add(paramState);
            }
        }

        return entry;
    }

    private void ApplyPipelinePreset(PipelinePresetEntry preset, VisualPipeline visualPipeline)
    {
        visualPipeline.ApplyPresetState(preset.Pipeline);

        _lfoEngine.Lfos.Clear();
        var lfoIdMap = new Dictionary<int, int>();
        foreach (var lfoState in preset.Lfos)
        {
            var lfo = _lfoEngine.AddLfo();
            lfo.WaveType = lfoState.WaveType;
            lfo.Frequency = MathF.Max(0.001f, lfoState.Frequency);
            lfo.Phase = lfoState.Phase;
            lfo.DutyCycle = Math.Clamp(lfoState.DutyCycle, 0.05f, 0.95f);
            lfo.SyncEnabled = lfoState.SyncEnabled;
            lfo.SyncSpeedMultiplier = lfoState.SyncSpeedMultiplier;
            lfoIdMap[lfoState.SourceId] = lfo.Id;
        }

        _fftSources.Clear();
        _nextFftSourceId = 1;
        var fftIdMap = new Dictionary<int, int>();
        foreach (var fftState in preset.FftSources)
        {
            var source = new FftModSource
            {
                Id = _nextFftSourceId++,
                BinCount = Math.Clamp(fftState.BinCount, 1, 64),
                Smoothing = Math.Clamp(fftState.Smoothing, 0f, 0.99f),
                SmoothedBins = new float[Math.Clamp(fftState.BinCount, 1, 64)]
            };

            _fftSources.Add(source);
            fftIdMap[fftState.SourceId] = source.Id;
        }

        if (_fftSources.Count == 0)
        {
            AddFftSource();
        }

        _modulationMatrix.Clear();
        _audioModulationMatrix.Clear();
        _lfoFftInteractionModes.Clear();

        var parameters = visualPipeline.Parameters;
        foreach (var mod in preset.ParameterModulations)
        {
            var parameter = ResolvePipelineParameter(parameters, mod.ParameterIndex, mod.ParameterName);
            if (parameter is null)
            {
                continue;
            }

            _lfoFftInteractionModes[parameter] = mod.InteractionMode;

            foreach (var lfoAssignment in mod.LfoAssignments)
            {
                if (!lfoIdMap.TryGetValue(lfoAssignment.SourceId, out var mappedLfoId))
                {
                    continue;
                }

                _modulationMatrix[(parameter, mappedLfoId)] = new LfoModulation
                {
                    Scale = lfoAssignment.Scale,
                    Offset = lfoAssignment.Offset
                };
            }

            foreach (var fftAssignment in mod.FftAssignments)
            {
                if (!fftIdMap.TryGetValue(fftAssignment.SourceId, out var mappedFftId))
                {
                    continue;
                }

                _audioModulationMatrix[(parameter, mappedFftId)] = new AudioModulation
                {
                    AudioBinIndex = Math.Max(0, fftAssignment.BinIndex),
                    Scale = fftAssignment.Scale,
                    Offset = fftAssignment.Offset
                };
            }
        }

        SanitizeAudioBinAssignments();
        _pipelinePresetStatus = $"Applied preset '{preset.Name}'.";
    }

    private static IParameter? ResolvePipelineParameter(IReadOnlyList<IParameter> parameters, int index, string name)
    {
        if (index >= 0 && index < parameters.Count && parameters[index].Name == name)
        {
            return parameters[index];
        }

        return parameters.FirstOrDefault(x => x.Name == name);
    }

    private void TryLoadPipelinePresetBankFromDisk()
    {
        try
        {
            if (!File.Exists(_pipelinePresetFilePath))
            {
                _pipelinePresetBank = new PipelinePresetBank();
                _pipelinePresetStatus = $"Preset file not found: {_pipelinePresetFilePath}";
                return;
            }

            var json = File.ReadAllText(_pipelinePresetFilePath);
            _pipelinePresetBank = JsonSerializer.Deserialize<PipelinePresetBank>(json, _jsonOptions) ?? new PipelinePresetBank();
            _selectedPipelinePresetIndex = Math.Clamp(_selectedPipelinePresetIndex, 0, Math.Max(0, _pipelinePresetBank.Presets.Count - 1));
            _pipelinePresetStatus = $"Loaded {_pipelinePresetBank.Presets.Count} preset(s).";

            if (_pipelinePresetBank.Presets.Count > 0)
            {
                _newPipelinePresetName = _pipelinePresetBank.Presets[_selectedPipelinePresetIndex].Name;
            }
        }
        catch (Exception ex)
        {
            _pipelinePresetStatus = $"Load failed: {ex.Message}";
        }
    }

    private void TrySavePipelinePresetBankToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(_pipelinePresetBank, _jsonOptions);
            File.WriteAllText(_pipelinePresetFilePath, json);
            _pipelinePresetStatus = $"Saved {_pipelinePresetBank.Presets.Count} preset(s) to {_pipelinePresetFilePath}";
        }
        catch (Exception ex)
        {
            _pipelinePresetStatus = $"Save failed: {ex.Message}";
        }
    }

    private void HandleVisualHotkeys()
    {
        var vDown = KeyboardState.IsKeyDown(Keys.V);
        if (vDown && !_vWasDown && _visuals.Count > 0)
        {
            _selectedVisualIndex = (_selectedVisualIndex + 1) % _visuals.Count;
            Console.WriteLine($"[Visual] Switched to: {_visuals[_selectedVisualIndex].Name}");
        }

        _vWasDown = vDown;
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
            AdjustBaseParameter(visual.Parameters[_selectedParameterIndex], -1, shiftDown);
        }

        if (rightDown && !_rightWasDown)
        {
            AdjustBaseParameter(visual.Parameters[_selectedParameterIndex], 1, shiftDown);
        }

        _tabWasDown = tabDown;
        _leftWasDown = leftDown;
        _rightWasDown = rightDown;
    }

    private static void AdjustBaseParameter(IParameter parameter, int direction, bool fast)
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

    private void RemoveLfoAndAssignments(int lfoId)
    {
        _lfoEngine.RemoveLfo(lfoId);

        var keysToRemove = _modulationMatrix.Keys.Where(k => k.LfoId == lfoId).ToArray();
        foreach (var key in keysToRemove)
        {
            _modulationMatrix.Remove(key);
        }
    }

    private void ApplyLfoToParameters()
    {
        foreach (var visual in _visuals)
        {
            for (var paramIndex = 0; paramIndex < visual.Parameters.Count; paramIndex++)
            {
                var parameter = visual.Parameters[paramIndex];

                var lfoSum = 0f;
                var hasLfo = false;
                foreach (var lfo in _lfoEngine.Lfos)
                {
                    var key = (parameter, lfo.Id);
                    if (!_modulationMatrix.TryGetValue(key, out var modulation) ||
                        !_lfoEngine.TryGetOutput(lfo.Id, out var lfoValue))
                    {
                        continue;
                    }

                    lfoSum += (lfoValue * modulation.Scale) + modulation.Offset;
                    hasLfo = true;
                }

                var fftSum = 0f;
                var hasFft = false;
                foreach (var fft in _fftSources)
                {
                    var audioKey = (parameter, fft.Id);
                    if (!_audioModulationMatrix.TryGetValue(audioKey, out var audioMod))
                    {
                        continue;
                    }

                    var audioValue = (GetAudioBinValue(fft.Id, audioMod.AudioBinIndex) * audioMod.Scale) + audioMod.Offset;
                    fftSum += audioValue;
                    hasFft = true;
                }

                var modulationFactor = 1f;
                if (hasLfo && hasFft)
                {
                    var mode = _lfoFftInteractionModes.GetValueOrDefault(parameter, ModulationInteractionMode.Add);
                    modulationFactor = mode switch
                    {
                        ModulationInteractionMode.Instead => fftSum,
                        ModulationInteractionMode.Add => lfoSum + fftSum,
                        ModulationInteractionMode.Subtract => lfoSum - fftSum,
                        ModulationInteractionMode.Multiply => lfoSum * fftSum,
                        _ => lfoSum + fftSum
                    };
                }
                else if (hasLfo)
                {
                    modulationFactor = lfoSum;
                }
                else if (hasFft)
                {
                    modulationFactor = fftSum;
                }

                parameter.ApplyCombinedModulation(modulationFactor);
            }
        }
    }

    private void PruneOrphanedParameterAssignments()
    {
        var liveParameters = new HashSet<IParameter>();
        foreach (var visual in _visuals)
        {
            foreach (var parameter in visual.Parameters)
            {
                liveParameters.Add(parameter);
            }
        }

        var lfoKeysToRemove = _modulationMatrix.Keys.Where(k => !liveParameters.Contains(k.Parameter)).ToArray();
        foreach (var key in lfoKeysToRemove)
        {
            _modulationMatrix.Remove(key);
        }

        var fftKeysToRemove = _audioModulationMatrix.Keys.Where(k => !liveParameters.Contains(k.Parameter)).ToArray();
        foreach (var key in fftKeysToRemove)
        {
            _audioModulationMatrix.Remove(key);
        }

        var modeKeysToRemove = _lfoFftInteractionModes.Keys.Where(k => !liveParameters.Contains(k)).ToArray();
        foreach (var key in modeKeysToRemove)
        {
            _lfoFftInteractionModes.Remove(key);
        }
    }

    private void UpdateAudioModulationBins()
    {
        var srcBins = _latestSpectrum.Length;

        foreach (var source in _fftSources)
        {
            if (source.BinCount <= 0)
            {
                source.BinCount = 1;
            }

            if (source.SmoothedBins.Length != source.BinCount)
            {
                Array.Resize(ref source.SmoothedBins, source.BinCount);
            }

            for (var outBin = 0; outBin < source.BinCount; outBin++)
            {
                var start = (outBin * srcBins) / source.BinCount;
                var end = ((outBin + 1) * srcBins) / source.BinCount;
                if (end <= start)
                {
                    end = Math.Min(srcBins, start + 1);
                }

                var sum = 0f;
                var count = 0;
                for (var i = start; i < end; i++)
                {
                    sum += NormalizeSpectrumDb(_latestSpectrum[i]);
                    count++;
                }

                var raw = count > 0 ? sum / count : 0f;
                var smoothing = Math.Clamp(source.Smoothing, 0f, 0.99f);
                source.SmoothedBins[outBin] = (source.SmoothedBins[outBin] * smoothing) + (raw * (1f - smoothing));
            }
        }
    }

    private float GetAudioBinValue(int fftId, int binIndex)
    {
        var source = _fftSources.FirstOrDefault(x => x.Id == fftId);
        if (source is null || source.SmoothedBins.Length == 0)
        {
            return 0f;
        }

        var clamped = Math.Clamp(binIndex, 0, source.SmoothedBins.Length - 1);
        return source.SmoothedBins[clamped];
    }

    private void AddFftSource()
    {
        var id = _nextFftSourceId++;
        _fftSources.Add(new FftModSource
        {
            Id = id,
            BinCount = 8,
            Smoothing = 0.75f,
            SmoothedBins = new float[8]
        });
    }

    private void RemoveFftSourceAndAssignments(int fftId)
    {
        _fftSources.RemoveAll(x => x.Id == fftId);

        var keysToRemove = _audioModulationMatrix.Keys.Where(k => k.FftId == fftId).ToArray();
        foreach (var key in keysToRemove)
        {
            _audioModulationMatrix.Remove(key);
        }
    }

    private void SanitizeAudioBinAssignments()
    {
        var sourceById = _fftSources.ToDictionary(x => x.Id, x => x);

        var missingSourceKeys = _audioModulationMatrix.Keys.Where(k => !sourceById.ContainsKey(k.FftId)).ToArray();
        foreach (var key in missingSourceKeys)
        {
            _audioModulationMatrix.Remove(key);
        }

        foreach (var entry in _audioModulationMatrix)
        {
            var source = sourceById[entry.Key.FftId];
            if (entry.Value.AudioBinIndex >= source.BinCount)
            {
                entry.Value.AudioBinIndex = source.BinCount - 1;
            }
        }
    }

    private static float NormalizeSpectrumDb(float db)
    {
        const float minDb = -100f;
        const float maxDb = 0f;
        var normalized = (db - minDb) / (maxDb - minDb);
        return Math.Clamp(normalized, 0f, 1f);
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

public static class Program
{
    public static void Main()
    {
        var gameWindowSettings = GameWindowSettings.Default;

        var outputWindowSettings = new NativeWindowSettings
        {
            ClientSize = new Vector2i(1280, 720),
            Title = "macViz - Output"
        };

        var controlSettings = new NativeWindowSettings
        {
            ClientSize = new Vector2i(1200, 900),
            Title = "macViz - Controls"
        };

        using var outputWindow = new MinimalGameWindow(gameWindowSettings, outputWindowSettings);
        using var controlWindow = new ControlPanelWindow(gameWindowSettings, controlSettings, outputWindow);
        outputWindow.AttachControlPanel(controlWindow);

        outputWindow.Run();
    }
}
