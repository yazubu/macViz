using ImGuiNET;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace macViz;

public partial class MinimalGameWindow
{
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

    private void DrawModMatrixWindow()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(320, 360), ImGuiCond.FirstUseEver);
        ImGui.Begin("Mod Matrix", ImGuiWindowFlags.AlwaysAutoResize);

        if (_visuals.Count > 0)
        {
            var activeVisual = _visuals[_selectedVisualIndex];
            DrawLfoAssignmentMatrix(activeVisual);
        }

        ImGui.End();
    }

    private void DrawFftPreviewWindow()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(320, 250), ImGuiCond.FirstUseEver);
        ImGui.Begin("FFT Preview", ImGuiWindowFlags.AlwaysAutoResize);
        DrawAudioModulationControls();
        ImGui.End();
    }

    private void DrawPipelineGraphWindow()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(1220, 12), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(760, 760), ImGuiCond.FirstUseEver);
        ImGui.Begin("Pipeline Graph Editor");

        if (_visuals.Count > 0)
        {
            var activeVisual = _visuals[_selectedVisualIndex];
            if (activeVisual is VisualPipeline visualPipeline && activeVisual is IVisualEditorPanel visualEditorPanel)
            {
                if (ImGui.CollapsingHeader("Pipeline Graph Editor", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    visualEditorPanel.DrawEditorPanel();
                }

                if (ImGui.CollapsingHeader("Modulation Graph", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    DrawLfoManagerControls();
                    ImGui.Separator();
                    DrawModulationGraphEditor(visualPipeline);
                }
            }
            else
            {
                ImGui.TextDisabled("Select 'Visual Pipeline' visual to edit the graph.");
            }
        }

        ImGui.End();
    }

    private void DrawPipelinePresetsWindow()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(1220, 790), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(760, 300), ImGuiCond.FirstUseEver);
        ImGui.Begin("Pipeline Presets");

        if (_visuals.Count > 0)
        {
            var activeVisual = _visuals[_selectedVisualIndex];
            if (activeVisual is VisualPipeline visualPipeline)
            {
                DrawPipelinePresetManager(visualPipeline);
            }
            else
            {
                ImGui.TextDisabled("Select 'Visual Pipeline' visual to manage presets.");
            }
        }

        ImGui.End();
    }

    private void DrawLfoManagerControls()
    {
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
                var removeSource = false;

                ImGui.BeginGroup();
                var bins = source.BinCount;
                if (ImGui.SliderInt("Bins", ref bins, 1, 64))
                {
                    source.BinCount = bins;
                    Array.Resize(ref source.SmoothedBins, source.BinCount);
                    source.VariabilityHistory.Clear();
                    SanitizeAudioBinAssignments();
                }

                var smoothing = source.Smoothing;
                if (ImGui.SliderFloat("Smoothing", ref smoothing, 0f, 0.99f, "%.2f"))
                {
                    source.Smoothing = Math.Clamp(smoothing, 0f, 0.99f);
                }

                var logarithmicGrouping = source.LogarithmicGrouping;
                if (ImGui.Checkbox("Logarithmic Bin Grouping", ref logarithmicGrouping))
                {
                    source.LogarithmicGrouping = logarithmicGrouping;
                    source.VariabilityHistory.Clear();
                }

                var expandVariability = source.ExpandVariability;
                if (ImGui.Checkbox("Expand Variability", ref expandVariability))
                {
                    source.ExpandVariability = expandVariability;
                    source.VariabilityHistory.Clear();
                }

                if (source.ExpandVariability)
                {
                    var variabilityWindowSeconds = source.VariabilityWindowSeconds;
                    if (ImGui.SliderFloat("Variability Window (s)", ref variabilityWindowSeconds, 0.2f, 10f, "%.1f s"))
                    {
                        source.VariabilityWindowSeconds = Math.Clamp(variabilityWindowSeconds, 0.2f, 10f);
                    }
                }

                if (ImGui.Button("Remove FFT Source"))
                {
                    removeSource = true;
                }

                ImGui.EndGroup();

                ImGui.SameLine();

                ImGui.BeginGroup();
                var expandLabel = source.ExpandVariability
                    ? $"On ({source.VariabilityWindowSeconds:F1}s)"
                    : "Off";
                var groupingLabel = source.LogarithmicGrouping ? "Log" : "Linear";
                ImGui.TextDisabled($"Preview | Bins: {source.BinCount} | Grouping: {groupingLabel} | Expand: {expandLabel}");

                ImGui.PlotHistogram(
                    "##fftVerticalBars",
                    ref source.SmoothedBins[0],
                    source.SmoothedBins.Length,
                    0,
                    string.Empty,
                    0f,
                    1f,
                    new System.Numerics.Vector2(300f, 140f));

                if (ImGui.IsItemHovered())
                {
                    var mouse = ImGui.GetIO().MousePos;
                    var min = ImGui.GetItemRectMin();
                    var max = ImGui.GetItemRectMax();
                    var width = MathF.Max(1f, max.X - min.X);
                    var t = Math.Clamp((mouse.X - min.X) / width, 0f, 0.9999f);
                    var bin = Math.Clamp((int)(t * source.SmoothedBins.Length), 0, source.SmoothedBins.Length - 1);
                    var value = source.SmoothedBins[bin];
                    ImGui.SetTooltip($"Bin {bin + 1}: {value:F2}");
                }

                ImGui.EndGroup();

                if (removeSource)
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
                    var currentLabel = LfoDisplayHelpers.GetSyncSpeedLabel(lfo.SyncSpeedMultiplier, SyncSpeedOptions);
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
    }

    private void DrawParameters(IVisual visual)
    {
        var sections = ParameterUiHelpers.BuildParameterSections(visual, ParameterUiHelpers.GetParameterSectionName);

        foreach (var (sectionName, parameterIndices) in sections)
        {
            ImGui.PushID($"param_section_{sectionName}");
            ImGui.SetNextItemOpen(false, ImGuiCond.FirstUseEver);

            if (ImGui.CollapsingHeader($"{sectionName} ({parameterIndices.Count})"))
            {
                for (var sectionIndex = 0; sectionIndex < parameterIndices.Count; sectionIndex++)
                {
                    var parameterIndex = parameterIndices[sectionIndex];
                    DrawParameterControl(visual, visual.Parameters[parameterIndex], parameterIndex);
                }
            }

            ImGui.PopID();
        }

        ImGui.TextDisabled("Tab: next parameter | Shift+Tab: previous | Left/Right: adjust base value");
    }

    private void DrawParameterControl(IVisual visual, IParameter parameter, int parameterIndex)
    {
        ImGui.PushID(parameterIndex);

        var isSelected = parameterIndex == _selectedParameterIndex;
        if (isSelected)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new System.Numerics.Vector4(0.25f, 0.35f, 0.55f, 1f));
        }

        var parameterLabel = ParameterUiHelpers.GetParameterDisplayName(parameter.Name);
        if (visual is VisualPipeline visualPipeline &&
            (ParameterUiHelpers.IsStageCoreParameter(parameter.Name) || parameter.Name.StartsWith("Mix Box / ", StringComparison.Ordinal)) &&
            visualPipeline.TryGetNodeDescriptorForParameter(parameter, out var nodeLabel))
        {
            parameterLabel = $"{nodeLabel} / {parameterLabel}";
        }

        switch (parameter)
        {
            case Parameter<int> intParameter:
            {
                var value = intParameter.Value;
                ImGui.SetNextItemWidth(220);
                if (ImGui.SliderInt(parameterLabel, ref value, intParameter.Min, intParameter.Max))
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
                if (ImGui.SliderFloat(parameterLabel, ref value, floatParameter.Min, floatParameter.Max))
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
        ImGui.PopID();
    }

    private void DrawAudioModulationControls()
    {
        ImGui.Text("Audio Modulation (FFT)");
        ImGui.TextDisabled("Each FFT source has independent bin count and smoothing.");

        if (_fftSources.Count == 0)
        {
            ImGui.TextDisabled("Add FFT Source in Modulation Graph.");
            return;
        }

        for (var i = 0; i < _fftSources.Count; i++)
        {
            var source = _fftSources[i];
            ImGui.PushID($"fft_preview_src_{source.Id}");

            if (ImGui.TreeNode($"FFT Source {source.Id} Preview"))
            {
                var expandLabel = source.ExpandVariability
                    ? $"On ({source.VariabilityWindowSeconds:F1}s)"
                    : "Off";
                var groupingLabel = source.LogarithmicGrouping ? "Log" : "Linear";
                ImGui.TextDisabled($"Bins: {source.BinCount} | Smoothing: {source.Smoothing:F2} | Grouping: {groupingLabel} | Expand: {expandLabel}");

                ImGui.PlotHistogram(
                    "##fftVerticalBars",
                    ref source.SmoothedBins[0],
                    source.SmoothedBins.Length,
                    0,
                    string.Empty,
                    0f,
                    1f,
                    new System.Numerics.Vector2(320f, 140f));

                if (ImGui.IsItemHovered())
                {
                    var mouse = ImGui.GetIO().MousePos;
                    var min = ImGui.GetItemRectMin();
                    var max = ImGui.GetItemRectMax();
                    var width = MathF.Max(1f, max.X - min.X);
                    var t = Math.Clamp((mouse.X - min.X) / width, 0f, 0.9999f);
                    var bin = Math.Clamp((int)(t * source.SmoothedBins.Length), 0, source.SmoothedBins.Length - 1);
                    var value = source.SmoothedBins[bin];
                    ImGui.SetTooltip($"Bin {bin + 1}: {value:F2}");
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

        var sections = ParameterUiHelpers.BuildParameterSections(visual, ParameterUiHelpers.GetModMatrixSectionName);
        var columns = 2 + _fftSources.Count + _lfoEngine.Lfos.Count;
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX;

        foreach (var (sectionName, sectionIndices) in sections)
        {
            ImGui.PushID($"mod_matrix_section_{sectionName}");
            ImGui.SetNextItemOpen(false, ImGuiCond.FirstUseEver);

            if (ImGui.CollapsingHeader($"{sectionName} ({sectionIndices.Count})"))
            {
                if (ImGui.BeginTable("LfoModMatrix", columns, flags, new System.Numerics.Vector2(1100, 320)))
                {
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

                    for (var sectionRow = 0; sectionRow < sectionIndices.Count; sectionRow++)
                    {
                        var row = sectionIndices[sectionRow];
                        DrawLfoAssignmentMatrixRow(visual, row, visual.Parameters[row]);
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.PopID();
        }
    }

    private void DrawLfoAssignmentMatrixRow(IVisual visual, int row, IParameter parameter)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.Text(ParameterUiHelpers.GetModMatrixParameterLabel(parameter));

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
            ParameterUiHelpers.AdjustBaseParameter(visual.Parameters[_selectedParameterIndex], -1, shiftDown);
        }

        if (rightDown && !_rightWasDown)
        {
            ParameterUiHelpers.AdjustBaseParameter(visual.Parameters[_selectedParameterIndex], 1, shiftDown);
        }

        _tabWasDown = tabDown;
        _leftWasDown = leftDown;
        _rightWasDown = rightDown;
    }

}
