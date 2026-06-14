using ImGuiNET;

namespace macViz;

public partial class MinimalGameWindow
{
    private enum ModGraphSourceKind
    {
        Lfo,
        Fft
    }

    private sealed class ModGraphSocket
    {
        public int Id;
        public IParameter? Parameter;
        public ModGraphSourceKind? SourceKind;
        public int? SourceId;
        public LfoModulation Lfo = new();
        public AudioModulation Fft = new();

        public bool IsConnected => SourceKind.HasValue && SourceId.HasValue;
    }

    private sealed class ModGraphTargetNode
    {
        public required string Key;
        public required string Label;
        public System.Numerics.Vector2 Position;
        public List<IParameter> Parameters = [];
        public List<ModGraphSocket> Sockets = [];
    }

    private readonly Dictionary<string, ModGraphTargetNode> _modGraphTargets = [];
    private int _nextModGraphSocketId = 1;
    private (ModGraphSourceKind Kind, int Id)? _modGraphLinkStart;
    private int? _modGraphInspectorSocketId;
    private string? _modGraphInspectorNodeKey;

    private void DrawModulationGraphEditor(VisualPipeline visualPipeline)
    {
        EnsureModGraphTargets(visualPipeline);
        SyncPipelineModulationMatricesFromGraph(visualPipeline);

        ImGui.Separator();
        ImGui.Text("Modulation Graph");
        ImGui.TextDisabled("Violet links: LFO/FFT source → node parameter socket");

        var childHeight = 290f;
        ImGui.BeginChild("mod_graph_canvas", new System.Numerics.Vector2(0f, childHeight), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar);

        var drawList = ImGui.GetWindowDrawList();
        var canvasMin = ImGui.GetCursorScreenPos();
        var canvasSize = ImGui.GetContentRegionAvail();
        var canvasMax = new System.Numerics.Vector2(canvasMin.X + canvasSize.X, canvasMin.Y + canvasSize.Y);

        drawList.AddRectFilled(canvasMin, canvasMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.07f, 0.07f, 0.1f, 1f)), 8f);

        var sourcePorts = new Dictionary<(ModGraphSourceKind Kind, int Id), System.Numerics.Vector2>();
        var sourceX = canvasMin.X + 20f;
        var sourceY = canvasMin.Y + 20f;

        DrawModGraphSourceColumn(drawList, sourceX, ref sourceY, "LFO", ModGraphSourceKind.Lfo, _lfoEngine.Lfos.Select(x => x.Id), sourcePorts);
        sourceY += 10f;
        DrawModGraphSourceColumn(drawList, sourceX, ref sourceY, "FFT", ModGraphSourceKind.Fft, _fftSources.Select(x => x.Id), sourcePorts);

        var targetBaseX = canvasMin.X + MathF.Max(260f, canvasSize.X * 0.35f);
        var targetY = canvasMin.Y + 20f;
        var violet = new System.Numerics.Vector4(0.72f, 0.42f, 1f, 1f);

        foreach (var target in _modGraphTargets.Values.OrderBy(x => x.Label, StringComparer.Ordinal))
        {
            EnsureSocketLayout(target);

            var height = 40f + (target.Sockets.Count * 22f);
            var nodeMin = new System.Numerics.Vector2(targetBaseX, targetY);
            var nodeMax = new System.Numerics.Vector2(targetBaseX + 360f, targetY + height);
            drawList.AddRectFilled(nodeMin, nodeMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.14f, 0.16f, 0.2f, 1f)), 8f);
            drawList.AddRect(nodeMin, nodeMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.35f, 0.4f, 0.5f, 1f)), 8f, ImDrawFlags.None, 1.5f);
            drawList.AddText(new System.Numerics.Vector2(nodeMin.X + 10f, nodeMin.Y + 8f), ImGui.GetColorU32(new System.Numerics.Vector4(1f, 1f, 1f, 1f)), target.Label);

            for (var i = 0; i < target.Sockets.Count; i++)
            {
                var socket = target.Sockets[i];
                var y = nodeMin.Y + 30f + (i * 22f);
                var socketPos = new System.Numerics.Vector2(nodeMin.X + 10f, y + 6f);
                var socketColor = socket.IsConnected ? violet : new System.Numerics.Vector4(0.35f, 0.2f, 0.45f, 1f);
                drawList.AddCircleFilled(socketPos, 5f, ImGui.GetColorU32(socketColor));

                var label = socket.Parameter is null
                    ? "(select parameter)"
                    : ParameterUiHelpers.GetModMatrixParameterLabel(socket.Parameter);
                drawList.AddText(new System.Numerics.Vector2(nodeMin.X + 22f, y), ImGui.GetColorU32(new System.Numerics.Vector4(0.9f, 0.9f, 0.95f, 1f)), label);

                ImGui.SetCursorScreenPos(new System.Numerics.Vector2(socketPos.X - 8f, socketPos.Y - 8f));
                ImGui.InvisibleButton($"mod_socket_{target.Key}_{socket.Id}", new System.Numerics.Vector2(16f, 16f));
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    if (_modGraphLinkStart.HasValue)
                    {
                        socket.SourceKind = _modGraphLinkStart.Value.Kind;
                        socket.SourceId = _modGraphLinkStart.Value.Id;
                        _modGraphInspectorSocketId = socket.Id;
                        _modGraphInspectorNodeKey = target.Key;
                        _modGraphLinkStart = null;
                    }
                    else
                    {
                        _modGraphInspectorSocketId = socket.Id;
                        _modGraphInspectorNodeKey = target.Key;
                    }
                }

                if (socket.IsConnected && socket.SourceKind.HasValue && socket.SourceId.HasValue &&
                    sourcePorts.TryGetValue((socket.SourceKind.Value, socket.SourceId.Value), out var sourcePort))
                {
                    drawList.AddBezierCubic(
                        sourcePort,
                        new System.Numerics.Vector2(sourcePort.X + 70f, sourcePort.Y),
                        new System.Numerics.Vector2(socketPos.X - 70f, socketPos.Y),
                        socketPos,
                        ImGui.GetColorU32(violet),
                        2f);
                }
            }

            targetY += height + 12f;
        }

        if (_modGraphLinkStart.HasValue && sourcePorts.TryGetValue(_modGraphLinkStart.Value, out var previewStart))
        {
            var mouse = ImGui.GetIO().MousePos;
            drawList.AddBezierCubic(
                previewStart,
                new System.Numerics.Vector2(previewStart.X + 70f, previewStart.Y),
                new System.Numerics.Vector2(mouse.X - 70f, mouse.Y),
                mouse,
                ImGui.GetColorU32(violet),
                2f);
        }

        ImGui.EndChild();
        DrawModGraphInspector();
    }

    private void DrawModGraphSourceColumn(
        ImDrawListPtr drawList,
        float x,
        ref float y,
        string groupLabel,
        ModGraphSourceKind sourceKind,
        IEnumerable<int> sourceIds,
        IDictionary<(ModGraphSourceKind Kind, int Id), System.Numerics.Vector2> sourcePorts)
    {
        drawList.AddText(new System.Numerics.Vector2(x, y), ImGui.GetColorU32(new System.Numerics.Vector4(0.8f, 0.8f, 0.9f, 1f)), groupLabel);
        y += 18f;

        foreach (var sourceId in sourceIds)
        {
            var nodeMin = new System.Numerics.Vector2(x, y);
            var nodeMax = new System.Numerics.Vector2(x + 200f, y + 28f);
            drawList.AddRectFilled(nodeMin, nodeMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.16f, 0.13f, 0.2f, 1f)), 6f);
            drawList.AddRect(nodeMin, nodeMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.45f, 0.3f, 0.65f, 1f)), 6f);
            drawList.AddText(new System.Numerics.Vector2(nodeMin.X + 8f, nodeMin.Y + 6f), ImGui.GetColorU32(new System.Numerics.Vector4(1f, 1f, 1f, 1f)), $"{groupLabel} {sourceId}");

            var output = new System.Numerics.Vector2(nodeMax.X - 10f, nodeMin.Y + 14f);
            drawList.AddCircleFilled(output, 6f, ImGui.GetColorU32(new System.Numerics.Vector4(0.72f, 0.42f, 1f, 1f)));
            sourcePorts[(sourceKind, sourceId)] = output;

            ImGui.SetCursorScreenPos(new System.Numerics.Vector2(output.X - 8f, output.Y - 8f));
            ImGui.InvisibleButton($"mod_src_out_{groupLabel}_{sourceId}", new System.Numerics.Vector2(16f, 16f));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _modGraphLinkStart = (sourceKind, sourceId);
            }

            y += 32f;
        }
    }

    private void DrawModGraphInspector()
    {
        if (!_modGraphInspectorSocketId.HasValue || string.IsNullOrWhiteSpace(_modGraphInspectorNodeKey))
        {
            return;
        }

        if (!_modGraphTargets.TryGetValue(_modGraphInspectorNodeKey, out var node))
        {
            _modGraphInspectorSocketId = null;
            _modGraphInspectorNodeKey = null;
            return;
        }

        var socket = node.Sockets.FirstOrDefault(x => x.Id == _modGraphInspectorSocketId.Value);
        if (socket is null)
        {
            _modGraphInspectorSocketId = null;
            _modGraphInspectorNodeKey = null;
            return;
        }

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(420f, 0f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"Mod Link Inspector##{node.Key}_{socket.Id}", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        ImGui.Text(node.Label);
        var selectedParamIndex = socket.Parameter is null ? -1 : node.Parameters.FindIndex(x => ReferenceEquals(x, socket.Parameter));
        var currentLabel = selectedParamIndex >= 0
            ? ParameterUiHelpers.GetModMatrixParameterLabel(node.Parameters[selectedParamIndex])
            : "Select parameter";

        if (ImGui.BeginCombo("Parameter", currentLabel))
        {
            for (var i = 0; i < node.Parameters.Count; i++)
            {
                var p = node.Parameters[i];
                var selected = i == selectedParamIndex;
                var label = ParameterUiHelpers.GetModMatrixParameterLabel(p);
                if (ImGui.Selectable(label, selected))
                {
                    socket.Parameter = p;
                    selectedParamIndex = i;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (socket.SourceKind == ModGraphSourceKind.Lfo)
        {
            var scale = socket.Lfo.Scale;
            if (ImGui.SliderFloat("Scale", ref scale, 0f, 2f, "%.2f"))
            {
                socket.Lfo.Scale = scale;
            }

            var offset = socket.Lfo.Offset;
            if (ImGui.SliderFloat("Offset", ref offset, -1f, 1f, "%.2f"))
            {
                socket.Lfo.Offset = offset;
            }
        }
        else if (socket.SourceKind == ModGraphSourceKind.Fft)
        {
            var fft = socket.SourceId.HasValue ? _fftSources.FirstOrDefault(x => x.Id == socket.SourceId.Value) : null;
            var maxBin = Math.Max(1, fft?.BinCount ?? 1);

            var bin = Math.Clamp(socket.Fft.AudioBinIndex, 0, maxBin - 1);
            if (ImGui.SliderInt("Bin", ref bin, 0, maxBin - 1, "Bin %d"))
            {
                socket.Fft.AudioBinIndex = bin;
            }

            var scale = socket.Fft.Scale;
            if (ImGui.SliderFloat("Scale", ref scale, 0f, 2f, "%.2f"))
            {
                socket.Fft.Scale = scale;
            }

            var offset = socket.Fft.Offset;
            if (ImGui.SliderFloat("Offset", ref offset, -1f, 1f, "%.2f"))
            {
                socket.Fft.Offset = offset;
            }
        }

        if (socket.Parameter is not null)
        {
            var interactionMode = _lfoFftInteractionModes.GetValueOrDefault(socket.Parameter, ModulationInteractionMode.Add);
            if (ImGui.BeginCombo("LFO↔FFT", interactionMode.ToString()))
            {
                foreach (ModulationInteractionMode candidate in Enum.GetValues<ModulationInteractionMode>())
                {
                    var selected = interactionMode == candidate;
                    if (ImGui.Selectable(candidate.ToString(), selected))
                    {
                        _lfoFftInteractionModes[socket.Parameter] = candidate;
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }
        }

        if (ImGui.Button("Disconnect"))
        {
            socket.SourceId = null;
            socket.SourceKind = null;
        }

        ImGui.SameLine();
        if (ImGui.Button("Close"))
        {
            _modGraphInspectorSocketId = null;
            _modGraphInspectorNodeKey = null;
        }

        ImGui.End();
    }

    private void EnsureModGraphTargets(VisualPipeline visualPipeline)
    {
        var byNode = new Dictionary<string, List<IParameter>>(StringComparer.Ordinal);
        foreach (var parameter in visualPipeline.Parameters)
        {
            if (!visualPipeline.TryGetNodeDescriptorForParameter(parameter, out var label))
            {
                label = "Pipeline";
            }

            if (!byNode.TryGetValue(label, out var parameters))
            {
                parameters = [];
                byNode[label] = parameters;
            }

            parameters.Add(parameter);
        }

        var deadKeys = _modGraphTargets.Keys.Where(k => !byNode.ContainsKey(k)).ToArray();
        foreach (var deadKey in deadKeys)
        {
            _modGraphTargets.Remove(deadKey);
        }

        var order = 0;
        foreach (var (key, parameters) in byNode.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!_modGraphTargets.TryGetValue(key, out var node))
            {
                node = new ModGraphTargetNode
                {
                    Key = key,
                    Label = key,
                    Position = new System.Numerics.Vector2(0f, order * 120f),
                    Parameters = parameters,
                    Sockets = []
                };
                _modGraphTargets[key] = node;
            }
            else
            {
                node.Parameters = parameters;
            }

            var parameterSet = node.Parameters.ToHashSet();
            foreach (var socket in node.Sockets)
            {
                if (socket.Parameter is not null && !parameterSet.Contains(socket.Parameter))
                {
                    socket.Parameter = null;
                }

                if (socket.SourceKind == ModGraphSourceKind.Lfo && socket.SourceId.HasValue && _lfoEngine.Lfos.All(x => x.Id != socket.SourceId.Value))
                {
                    socket.SourceKind = null;
                    socket.SourceId = null;
                }

                if (socket.SourceKind == ModGraphSourceKind.Fft && socket.SourceId.HasValue && _fftSources.All(x => x.Id != socket.SourceId.Value))
                {
                    socket.SourceKind = null;
                    socket.SourceId = null;
                }
            }

            EnsureSocketLayout(node);
            order++;
        }
    }

    private void EnsureSocketLayout(ModGraphTargetNode node)
    {
        if (node.Sockets.Count == 0)
        {
            node.Sockets.Add(NewModGraphSocket());
            return;
        }

        var emptySocketCount = node.Sockets.Count(x => !x.IsConnected);
        if (emptySocketCount == 0)
        {
            node.Sockets.Add(NewModGraphSocket());
        }
        else if (emptySocketCount > 1)
        {
            var keepOne = false;
            node.Sockets.RemoveAll(x => !x.IsConnected && (keepOne || !(keepOne = true)));
            if (!node.Sockets.Any(x => !x.IsConnected))
            {
                node.Sockets.Add(NewModGraphSocket());
            }
        }
    }

    private ModGraphSocket NewModGraphSocket()
    {
        return new ModGraphSocket
        {
            Id = _nextModGraphSocketId++,
            Lfo = new LfoModulation(),
            Fft = new AudioModulation()
        };
    }

    private void SyncPipelineModulationMatricesFromGraph(VisualPipeline visualPipeline)
    {
        var pipelineParams = visualPipeline.Parameters.ToHashSet();

        var lfoKeysToRemove = _modulationMatrix.Keys.Where(x => pipelineParams.Contains(x.Parameter)).ToArray();
        foreach (var key in lfoKeysToRemove)
        {
            _modulationMatrix.Remove(key);
        }

        var fftKeysToRemove = _audioModulationMatrix.Keys.Where(x => pipelineParams.Contains(x.Parameter)).ToArray();
        foreach (var key in fftKeysToRemove)
        {
            _audioModulationMatrix.Remove(key);
        }

        foreach (var node in _modGraphTargets.Values)
        {
            foreach (var socket in node.Sockets)
            {
                if (!socket.IsConnected || socket.Parameter is null || !socket.SourceKind.HasValue || !socket.SourceId.HasValue)
                {
                    continue;
                }

                if (socket.SourceKind == ModGraphSourceKind.Lfo)
                {
                    _modulationMatrix[(socket.Parameter, socket.SourceId.Value)] = new LfoModulation
                    {
                        Scale = socket.Lfo.Scale,
                        Offset = socket.Lfo.Offset
                    };
                }
                else if (socket.SourceKind == ModGraphSourceKind.Fft)
                {
                    _audioModulationMatrix[(socket.Parameter, socket.SourceId.Value)] = new AudioModulation
                    {
                        AudioBinIndex = socket.Fft.AudioBinIndex,
                        Scale = socket.Fft.Scale,
                        Offset = socket.Fft.Offset
                    };
                }
            }
        }

        SanitizeAudioBinAssignments();
    }

    private void RebuildModulationGraphFromAssignments(VisualPipeline visualPipeline)
    {
        EnsureModGraphTargets(visualPipeline);

        foreach (var node in _modGraphTargets.Values)
        {
            node.Sockets.Clear();
            node.Sockets.Add(NewModGraphSocket());
        }

        var nodeForParameter = new Dictionary<IParameter, ModGraphTargetNode>();
        foreach (var node in _modGraphTargets.Values)
        {
            foreach (var parameter in node.Parameters)
            {
                nodeForParameter[parameter] = node;
            }
        }

        foreach (var ((parameter, lfoId), lfoMod) in _modulationMatrix)
        {
            if (!nodeForParameter.TryGetValue(parameter, out var node))
            {
                continue;
            }

            var socket = NewModGraphSocket();
            socket.Parameter = parameter;
            socket.SourceKind = ModGraphSourceKind.Lfo;
            socket.SourceId = lfoId;
            socket.Lfo.Scale = lfoMod.Scale;
            socket.Lfo.Offset = lfoMod.Offset;
            node.Sockets.Insert(node.Sockets.Count - 1, socket);
        }

        foreach (var ((parameter, fftId), fftMod) in _audioModulationMatrix)
        {
            if (!nodeForParameter.TryGetValue(parameter, out var node))
            {
                continue;
            }

            var socket = NewModGraphSocket();
            socket.Parameter = parameter;
            socket.SourceKind = ModGraphSourceKind.Fft;
            socket.SourceId = fftId;
            socket.Fft.AudioBinIndex = fftMod.AudioBinIndex;
            socket.Fft.Scale = fftMod.Scale;
            socket.Fft.Offset = fftMod.Offset;
            node.Sockets.Insert(node.Sockets.Count - 1, socket);
        }

        foreach (var node in _modGraphTargets.Values)
        {
            EnsureSocketLayout(node);
        }
    }
}
