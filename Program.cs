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

    private int _selectedVisualIndex;
    private int _selectedParameterIndex;
    private readonly Dictionary<(int VisualIndex, int ParamIndex, int LfoId), LfoModulation> _modulationMatrix = new();
    private readonly Dictionary<(int VisualIndex, int ParamIndex, int FftId), AudioModulation> _audioModulationMatrix = new();
    private readonly Dictionary<(int VisualIndex, int ParamIndex), ModulationInteractionMode> _lfoFftInteractionModes = new();
    private readonly List<FftModSource> _fftSources = [];
    private int _nextFftSourceId = 1;

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

    protected override void OnLoad()
    {
        base.OnLoad();
        GL.ClearColor(new Color4(0f, 0f, 0.2f, 1f));
        _imGuiController = new ImGuiController(ClientSize.X, ClientSize.Y, this);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);

        _visuals.Add(new SpectrumBars2d());
        _visuals.Add(new RotatingCube3D());
        _visuals.Add(new RotatingParticleSystem3D());

        _visuals.Add(new CameraFilterGrayscale());
        _visuals.Add(new CameraFilterEdgeDetection());

        _selectedVisualIndex = Math.Min(1, _visuals.Count - 1); // Start on cube if available.

        AddFftSource();

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

        _imGuiController!.Update(deltaTime);
        _elapsedTime += args.Time;

        _internalTempo.Update(deltaTime);
        _abletonLink.Update(deltaTime);
        _lfoEngine.Update(deltaTime, GetActiveBeatClock());

        UpdateSpectrumData();
        UpdateAudioModulationBins();
        ApplyLfoToParameters();
        HandleVisualHotkeys();

        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (_visuals.Count > 0)
        {
            _visuals[_selectedVisualIndex].Render(_latestSpectrum, (float)_elapsedTime);
        }

        ImGui.NewFrame();

        DrawParametersWindow();
        DrawLfoManagerWindow();

        ImGui.Render();
        _imGuiController.RenderDrawData(ImGui.GetDrawData());

        SwapBuffers();
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
                    _selectedParameterIndex = 0;
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

            if (activeVisual is ICameraVisual cameraVisual)
            {
                DrawCameraControls(cameraVisual);
            }

            DrawAudioModulationControls();
            DrawLfoAssignmentMatrix(activeVisual, _selectedVisualIndex);
        }

        if (!string.IsNullOrWhiteSpace(_audioInitError))
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f), $"Audio init failed: {_audioInitError}");
        }

        ImGui.End();
    }

    private void DrawLfoManagerWindow()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(12, 330), ImGuiCond.Once);
        ImGui.Begin("LFO Manager", ImGuiWindowFlags.AlwaysAutoResize);

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

    private void DrawLfoAssignmentMatrix(IVisual visual, int visualIndex)
    {
        ImGui.Text("Mod Matrix");
        ImGui.TextDisabled("Assigned source output multiplies the base parameter value.");

        var columns = 2 + _fftSources.Count + _lfoEngine.Lfos.Count;
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX;
        if (!ImGui.BeginTable("LfoModMatrix", columns, flags, new System.Numerics.Vector2(1100, 320)))
        {
            return;
        }

        ImGui.TableSetupColumn("Parameter", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableSetupColumn("LFO↔FFT", ImGuiTableColumnFlags.WidthFixed, 130);

        foreach (var fft in _fftSources)
        {
            ImGui.TableSetupColumn($"FFT {fft.Id}", ImGuiTableColumnFlags.WidthFixed, 220);
        }

        foreach (var lfo in _lfoEngine.Lfos)
        {
            ImGui.TableSetupColumn($"LFO {lfo.Id}", ImGuiTableColumnFlags.WidthFixed, 170);
        }

        ImGui.TableHeadersRow();

        for (var row = 0; row < visual.Parameters.Count; row++)
        {
            var parameter = visual.Parameters[row];

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text(parameter.Name);

            ImGui.TableSetColumnIndex(1);
            ImGui.PushID($"mode_{visualIndex}_{row}");
            var interactionKey = (visualIndex, row);
            var interactionMode = _lfoFftInteractionModes.GetValueOrDefault(interactionKey, ModulationInteractionMode.Add);
            if (ImGui.BeginCombo("##interaction", interactionMode.ToString()))
            {
                foreach (ModulationInteractionMode candidate in Enum.GetValues<ModulationInteractionMode>())
                {
                    var isSelected = candidate == interactionMode;
                    if (ImGui.Selectable(candidate.ToString(), isSelected))
                    {
                        _lfoFftInteractionModes[interactionKey] = candidate;
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
                var audioKey = (visualIndex, row, fft.Id);
                ImGui.TableSetColumnIndex(fftCol + 2);
                ImGui.PushID($"fft_cell_{visualIndex}_{row}_{fft.Id}");

                var fftAssigned = _audioModulationMatrix.TryGetValue(audioKey, out var audioMod);
                if (ImGui.Checkbox("Assign", ref fftAssigned))
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
                    ImGui.SetNextItemWidth(170);
                    if (ImGui.SliderFloat("##fftScale", ref audioScale, 0f, 2f, "Scale %.2f"))
                    {
                        audioMod.Scale = audioScale;
                    }

                    var audioOffset = audioMod.Offset;
                    ImGui.SetNextItemWidth(170);
                    if (ImGui.SliderFloat("##fftOffset", ref audioOffset, -1f, 1f, "Offset %.2f"))
                    {
                        audioMod.Offset = audioOffset;
                    }

                    var binValue = GetAudioBinValue(fft.Id, audioMod.AudioBinIndex);
                    ImGui.TextDisabled($"FFT: {binValue:F2}");
                }

                ImGui.PopID();
            }

            for (var lfoCol = 0; lfoCol < _lfoEngine.Lfos.Count; lfoCol++)
            {
                var lfo = _lfoEngine.Lfos[lfoCol];
                var key = (visualIndex, row, lfo.Id);

                ImGui.TableSetColumnIndex(lfoCol + 2 + _fftSources.Count);
                ImGui.PushID($"cell_{visualIndex}_{row}_{lfo.Id}");

                var assigned = _modulationMatrix.TryGetValue(key, out var modulation);
                if (ImGui.Checkbox("Assign", ref assigned))
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
                    ImGui.SetNextItemWidth(150);
                    if (ImGui.SliderFloat("##scale", ref scale, 0f, 2f, "Scale %.2f"))
                    {
                        modulation.Scale = scale;
                    }

                    var offset = modulation.Offset;
                    ImGui.SetNextItemWidth(150);
                    if (ImGui.SliderFloat("##offset", ref offset, -1f, 1f, "Offset %.2f"))
                    {
                        modulation.Offset = offset;
                    }
                }

                ImGui.PopID();
            }
        }

        ImGui.EndTable();
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
        for (var visualIndex = 0; visualIndex < _visuals.Count; visualIndex++)
        {
            var visual = _visuals[visualIndex];
            for (var paramIndex = 0; paramIndex < visual.Parameters.Count; paramIndex++)
            {
                var parameter = visual.Parameters[paramIndex];

                var lfoSum = 0f;
                var hasLfo = false;
                foreach (var lfo in _lfoEngine.Lfos)
                {
                    var key = (visualIndex, paramIndex, lfo.Id);
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
                    var audioKey = (visualIndex, paramIndex, fft.Id);
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
                    var mode = _lfoFftInteractionModes.GetValueOrDefault((visualIndex, paramIndex), ModulationInteractionMode.Add);
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
        _imGuiController?.WindowResized(e.Width, e.Height);
    }

    protected override void OnUnload()
    {
        _audioSpectrum?.Dispose();
        _abletonLink.Dispose();
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
