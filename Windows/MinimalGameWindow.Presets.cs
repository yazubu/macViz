using System.Text.Json;
using ImGuiNET;

namespace macViz;

public partial class MinimalGameWindow
{
    private void DrawPipelinePresetManager(VisualPipeline visualPipeline)
    {
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
        EnsureModGraphTargets(visualPipeline);

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
                Smoothing = fft.Smoothing,
                LogarithmicGrouping = fft.LogarithmicGrouping,
                ExpandVariability = fft.ExpandVariability,
                VariabilityWindowSeconds = fft.VariabilityWindowSeconds
            });
        }

        foreach (var target in _modGraphTargets.Values.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            entry.ModGraphNodePositions.Add(new ModGraphNodePositionDto
            {
                NodeKey = target.Key,
                X = target.Position.X,
                Y = target.Position.Y
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
                LogarithmicGrouping = fftState.LogarithmicGrouping,
                ExpandVariability = fftState.ExpandVariability,
                VariabilityWindowSeconds = Math.Clamp(fftState.VariabilityWindowSeconds, 0.2f, 10f),
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
            var parameter = ParameterUiHelpers.ResolvePipelineParameter(parameters, mod.ParameterIndex, mod.ParameterName);
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
        RebuildModulationGraphFromAssignments(visualPipeline);

        if (preset.ModGraphNodePositions.Count > 0)
        {
            foreach (var nodePos in preset.ModGraphNodePositions)
            {
                if (_modGraphTargets.TryGetValue(nodePos.NodeKey, out var node))
                {
                    node.Position = new System.Numerics.Vector2(nodePos.X, nodePos.Y);
                }
            }
        }

        _pipelinePresetStatus = $"Applied preset '{preset.Name}'.";
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
}
