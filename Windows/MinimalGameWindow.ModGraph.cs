using ImGuiNET;

namespace macViz;

public partial class MinimalGameWindow
{
    private enum ModGraphSourceKind
    {
        Lfo,
        Fft,
        Processor
    }

    private enum ModGraphProcessorKind
    {
        Invert,
        Absolute,
        Multiply,
        Constant,
        Add
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

    private sealed class ModGraphProcessorNode
    {
        public int Id;
        public ModGraphProcessorKind Kind;
        public System.Numerics.Vector2 Position;
        public List<ModGraphSocket> Inputs = [];
        public float ConstantValue = 1f;
    }

    private readonly Dictionary<string, ModGraphTargetNode> _modGraphTargets = [];
    private readonly Dictionary<int, ModGraphProcessorNode> _modGraphProcessors = [];
    private int _nextModGraphSocketId = 1;
    private int _nextModGraphProcessorId = 1;
    private (ModGraphSourceKind Kind, int Id)? _modGraphLinkStart;
    private int? _modGraphInspectorSocketId;
    private string? _modGraphInspectorNodeKey;
    private System.Numerics.Vector2 _modGraphCanvasPan = new(24f, 24f);

    private void DrawModulationGraphEditor(VisualPipeline visualPipeline)
    {
        EnsureModGraphTargets(visualPipeline);
        SyncPipelineModulationMatricesFromGraph(visualPipeline);

        if (ImGui.Button("Add Invert")) AddModGraphProcessorNode(ModGraphProcessorKind.Invert);
        ImGui.SameLine();
        if (ImGui.Button("Add Absolute")) AddModGraphProcessorNode(ModGraphProcessorKind.Absolute);
        ImGui.SameLine();
        if (ImGui.Button("Add Multiply")) AddModGraphProcessorNode(ModGraphProcessorKind.Multiply);
        ImGui.SameLine();
        if (ImGui.Button("Add Constant")) AddModGraphProcessorNode(ModGraphProcessorKind.Constant);
        ImGui.SameLine();
        if (ImGui.Button("Add Add")) AddModGraphProcessorNode(ModGraphProcessorKind.Add);

        ImGui.TextDisabled("Violet links: source/output → input socket. Pan: right/middle drag or Space+left drag. Drag node headers to move. Double-click link to disconnect.");

        var childHeight = 580f;
        ImGui.BeginChild("mod_graph_canvas", new System.Numerics.Vector2(0f, childHeight), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        var drawList = ImGui.GetWindowDrawList();
        var canvasMin = ImGui.GetCursorScreenPos();
        var canvasSize = ImGui.GetContentRegionAvail();
        var canvasMax = new System.Numerics.Vector2(canvasMin.X + canvasSize.X, canvasMin.Y + canvasSize.Y);

        var io = ImGui.GetIO();
        var mousePos = io.MousePos;
        var canvasHovered =
            ImGui.IsWindowHovered() &&
            mousePos.X >= canvasMin.X && mousePos.X <= canvasMax.X &&
            mousePos.Y >= canvasMin.Y && mousePos.Y <= canvasMax.Y;

        if (canvasHovered && _modGraphLinkStart.HasValue && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _modGraphLinkStart = null;
        }

        var isSpaceDown = ImGui.IsKeyDown(ImGuiKey.Space);
        var isPanningCanvas = canvasHovered &&
            (ImGui.IsMouseDragging(ImGuiMouseButton.Right)
             || ImGui.IsMouseDragging(ImGuiMouseButton.Middle)
             || (isSpaceDown && ImGui.IsMouseDragging(ImGuiMouseButton.Left)));

        if (isPanningCanvas)
        {
            _modGraphCanvasPan.X += io.MouseDelta.X;
            _modGraphCanvasPan.Y += io.MouseDelta.Y;
        }

        drawList.AddRectFilled(canvasMin, canvasMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.07f, 0.07f, 0.1f, 1f)), 8f);

        var sourcePorts = new Dictionary<(ModGraphSourceKind Kind, int Id), System.Numerics.Vector2>();
        var drawnLinks = new List<(bool IsProcessorInput, int ProcessorId, string NodeKey, int SocketId, System.Numerics.Vector2 Start, System.Numerics.Vector2 C1, System.Numerics.Vector2 C2, System.Numerics.Vector2 End)>();
        var sourceX = canvasMin.X + 20f + _modGraphCanvasPan.X;
        var sourceY = canvasMin.Y + 20f + _modGraphCanvasPan.Y;

        DrawModGraphSourceColumn(drawList, sourceX, ref sourceY, "LFO", ModGraphSourceKind.Lfo, _lfoEngine.Lfos.Select(x => x.Id), sourcePorts);
        sourceY += 10f;
        DrawModGraphSourceColumn(drawList, sourceX, ref sourceY, "FFT", ModGraphSourceKind.Fft, _fftSources.Select(x => x.Id), sourcePorts);

        DrawModGraphProcessorNodes(drawList, canvasMin, sourcePorts, drawnLinks, io, isSpaceDown);

        var targetOrigin = new System.Numerics.Vector2(
            MathF.Max(520f, canvasSize.X * 0.45f),
            20f);
        DrawModGraphTargetNodes(drawList, canvasMin, sourcePorts, drawnLinks, io, isSpaceDown, targetOrigin);

        if (canvasHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            if (TryDisconnectModGraphLinkAtMouse(mousePos, drawnLinks))
            {
                _modGraphLinkStart = null;
            }
            else if (_modGraphLinkStart.HasValue)
            {
                _modGraphLinkStart = null;
            }
        }

        if (_modGraphLinkStart.HasValue && sourcePorts.TryGetValue(_modGraphLinkStart.Value, out var previewStart))
        {
            var mouse = ImGui.GetIO().MousePos;
            drawList.AddBezierCubic(
                previewStart,
                new System.Numerics.Vector2(previewStart.X + 70f, previewStart.Y),
                new System.Numerics.Vector2(mouse.X - 70f, mouse.Y),
                mouse,
                ImGui.GetColorU32(new System.Numerics.Vector4(0.72f, 0.42f, 1f, 1f)),
                2f);
        }

        ImGui.EndChild();
        DrawModGraphInspector();
    }

    private void DrawModGraphProcessorNodes(
        ImDrawListPtr drawList,
        System.Numerics.Vector2 canvasMin,
        IDictionary<(ModGraphSourceKind Kind, int Id), System.Numerics.Vector2> sourcePorts,
        ICollection<(bool IsProcessorInput, int ProcessorId, string NodeKey, int SocketId, System.Numerics.Vector2 Start, System.Numerics.Vector2 C1, System.Numerics.Vector2 C2, System.Numerics.Vector2 End)> drawnLinks,
        ImGuiIOPtr io,
        bool isSpaceDown)
    {
        var violet = new System.Numerics.Vector4(0.72f, 0.42f, 1f, 1f);
        var processorOrigin = new System.Numerics.Vector2(300f, 20f);
        int? pendingRemoveProcessorId = null;

        foreach (var processor in _modGraphProcessors.Values.OrderBy(x => x.Id))
        {
            ImGui.PushID($"mod_proc_{processor.Id}");
            EnsureProcessorInputLayout(processor);

            var height = MathF.Max(62f, 36f + (processor.Inputs.Count * 22f));
            var nodeMin = new System.Numerics.Vector2(
                canvasMin.X + _modGraphCanvasPan.X + processorOrigin.X + processor.Position.X,
                canvasMin.Y + _modGraphCanvasPan.Y + processorOrigin.Y + processor.Position.Y);
            var nodeMax = new System.Numerics.Vector2(nodeMin.X + 260f, nodeMin.Y + height);

            drawList.AddRectFilled(nodeMin, nodeMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.17f, 0.14f, 0.22f, 1f)), 8f);
            drawList.AddRect(nodeMin, nodeMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.45f, 0.3f, 0.65f, 1f)), 8f, ImDrawFlags.None, 1.5f);
            drawList.AddText(new System.Numerics.Vector2(nodeMin.X + 10f, nodeMin.Y + 8f), ImGui.GetColorU32(new System.Numerics.Vector4(1f, 1f, 1f, 1f)), $"{processor.Kind} [{processor.Id}]");

            ImGui.SetCursorScreenPos(new System.Numerics.Vector2(nodeMin.X + 8f, nodeMin.Y + 4f));
            ImGui.InvisibleButton("proc_drag", new System.Numerics.Vector2(210f, 22f));
            if (!isSpaceDown && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                processor.Position.X += io.MouseDelta.X;
                processor.Position.Y += io.MouseDelta.Y;
            }

            ImGui.SetCursorScreenPos(new System.Numerics.Vector2(nodeMax.X - 42f, nodeMin.Y + 3f));
            if (ImGui.SmallButton("X"))
            {
                pendingRemoveProcessorId = processor.Id;
                ImGui.PopID();
                break;
            }

            if (processor.Kind == ModGraphProcessorKind.Constant)
            {
                var value = processor.ConstantValue;
                ImGui.SetCursorScreenPos(new System.Numerics.Vector2(nodeMin.X + 10f, nodeMin.Y + 32f));
                ImGui.SetNextItemWidth(200f);
                if (ImGui.SliderFloat("##const", ref value, -2f, 2f, "Const %.2f"))
                {
                    processor.ConstantValue = value;
                }
            }
            else
            {
                for (var i = 0; i < processor.Inputs.Count; i++)
                {
                    var input = processor.Inputs[i];
                    var y = nodeMin.Y + 30f + (i * 22f);
                    var inputPos = new System.Numerics.Vector2(nodeMin.X + 10f, y + 6f);
                    var color = input.IsConnected ? violet : new System.Numerics.Vector4(0.35f, 0.2f, 0.45f, 1f);
                    drawList.AddCircleFilled(inputPos, 5f, ImGui.GetColorU32(color));
                    drawList.AddText(new System.Numerics.Vector2(nodeMin.X + 22f, y), ImGui.GetColorU32(new System.Numerics.Vector4(0.9f, 0.9f, 0.95f, 1f)), $"In {i + 1}");

                    ImGui.SetCursorScreenPos(new System.Numerics.Vector2(inputPos.X - 8f, inputPos.Y - 8f));
                    ImGui.InvisibleButton($"proc_in_{input.Id}", new System.Numerics.Vector2(16f, 16f));
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && _modGraphLinkStart.HasValue)
                    {
                        input.SourceKind = _modGraphLinkStart.Value.Kind;
                        input.SourceId = _modGraphLinkStart.Value.Id;
                        _modGraphLinkStart = null;
                    }

                    if (input.IsConnected && input.SourceKind.HasValue && input.SourceId.HasValue &&
                        sourcePorts.TryGetValue((input.SourceKind.Value, input.SourceId.Value), out var sourcePort))
                    {
                        var c1 = new System.Numerics.Vector2(sourcePort.X + 70f, sourcePort.Y);
                        var c2 = new System.Numerics.Vector2(inputPos.X - 70f, inputPos.Y);
                        drawList.AddBezierCubic(sourcePort, c1, c2, inputPos, ImGui.GetColorU32(violet), 2f);
                        drawnLinks.Add((true, processor.Id, string.Empty, input.Id, sourcePort, c1, c2, inputPos));
                    }
                }

                if (processor.Kind is ModGraphProcessorKind.Add or ModGraphProcessorKind.Multiply)
                {
                    ImGui.SetCursorScreenPos(new System.Numerics.Vector2(nodeMin.X + 175f, nodeMin.Y + 4f));
                    if (ImGui.SmallButton("+In"))
                    {
                        processor.Inputs.Add(NewModGraphSocket());
                    }
                }
            }

            var outputPos = new System.Numerics.Vector2(nodeMax.X - 10f, nodeMin.Y + (height * 0.5f));
            drawList.AddCircleFilled(outputPos, 6f, ImGui.GetColorU32(violet));
            sourcePorts[(ModGraphSourceKind.Processor, processor.Id)] = outputPos;

            ImGui.SetCursorScreenPos(new System.Numerics.Vector2(outputPos.X - 8f, outputPos.Y - 8f));
            ImGui.InvisibleButton("proc_out", new System.Numerics.Vector2(16f, 16f));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _modGraphLinkStart = (ModGraphSourceKind.Processor, processor.Id);
            }

            ImGui.PopID();
        }

        if (pendingRemoveProcessorId.HasValue)
        {
            RemoveModGraphProcessorNode(pendingRemoveProcessorId.Value);
        }
    }

    private void DrawModGraphTargetNodes(
        ImDrawListPtr drawList,
        System.Numerics.Vector2 canvasMin,
        IDictionary<(ModGraphSourceKind Kind, int Id), System.Numerics.Vector2> sourcePorts,
        ICollection<(bool IsProcessorInput, int ProcessorId, string NodeKey, int SocketId, System.Numerics.Vector2 Start, System.Numerics.Vector2 C1, System.Numerics.Vector2 C2, System.Numerics.Vector2 End)> drawnLinks,
        ImGuiIOPtr io,
        bool isSpaceDown,
        System.Numerics.Vector2 targetOrigin)
    {
        var violet = new System.Numerics.Vector4(0.72f, 0.42f, 1f, 1f);

        foreach (var target in _modGraphTargets.Values.OrderBy(x => x.Label, StringComparer.Ordinal))
        {
            ImGui.PushID(target.Key);
            EnsureSocketLayout(target);

            var height = 40f + (target.Sockets.Count * 22f);
            var nodeMin = new System.Numerics.Vector2(
                canvasMin.X + _modGraphCanvasPan.X + targetOrigin.X + target.Position.X,
                canvasMin.Y + _modGraphCanvasPan.Y + targetOrigin.Y + target.Position.Y);
            var nodeMax = new System.Numerics.Vector2(nodeMin.X + 360f, nodeMin.Y + height);
            drawList.AddRectFilled(nodeMin, nodeMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.14f, 0.16f, 0.2f, 1f)), 8f);
            drawList.AddRect(nodeMin, nodeMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.35f, 0.4f, 0.5f, 1f)), 8f, ImDrawFlags.None, 1.5f);
            drawList.AddText(new System.Numerics.Vector2(nodeMin.X + 10f, nodeMin.Y + 8f), ImGui.GetColorU32(new System.Numerics.Vector4(1f, 1f, 1f, 1f)), target.Label);

            ImGui.SetCursorScreenPos(new System.Numerics.Vector2(nodeMin.X + 8f, nodeMin.Y + 4f));
            ImGui.InvisibleButton("target_drag", new System.Numerics.Vector2(344f, 22f));
            if (!isSpaceDown && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                target.Position.X += io.MouseDelta.X;
                target.Position.Y += io.MouseDelta.Y;
            }

            for (var i = 0; i < target.Sockets.Count; i++)
            {
                var socket = target.Sockets[i];
                ImGui.PushID(socket.Id);
                var y = nodeMin.Y + 30f + (i * 22f);
                var socketPos = new System.Numerics.Vector2(nodeMin.X + 10f, y + 6f);
                var socketColor = socket.IsConnected ? violet : new System.Numerics.Vector4(0.35f, 0.2f, 0.45f, 1f);
                drawList.AddCircleFilled(socketPos, 5f, ImGui.GetColorU32(socketColor));

                var label = socket.Parameter is null
                    ? "(select parameter)"
                    : ParameterUiHelpers.GetModMatrixParameterLabel(socket.Parameter);
                drawList.AddText(new System.Numerics.Vector2(nodeMin.X + 22f, y), ImGui.GetColorU32(new System.Numerics.Vector4(0.9f, 0.9f, 0.95f, 1f)), label);

                ImGui.SetCursorScreenPos(new System.Numerics.Vector2(socketPos.X - 8f, socketPos.Y - 8f));
                ImGui.InvisibleButton("mod_socket", new System.Numerics.Vector2(16f, 16f));
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
                    var c1 = new System.Numerics.Vector2(sourcePort.X + 70f, sourcePort.Y);
                    var c2 = new System.Numerics.Vector2(socketPos.X - 70f, socketPos.Y);
                    drawList.AddBezierCubic(sourcePort, c1, c2, socketPos, ImGui.GetColorU32(violet), 2f);
                    drawnLinks.Add((false, 0, target.Key, socket.Id, sourcePort, c1, c2, socketPos));
                }

                ImGui.PopID();
            }

            ImGui.PopID();
        }
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
            ImGui.PushID($"{groupLabel}_{sourceId}");
            var nodeMin = new System.Numerics.Vector2(x, y);
            var nodeMax = new System.Numerics.Vector2(x + 200f, y + 28f);
            drawList.AddRectFilled(nodeMin, nodeMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.16f, 0.13f, 0.2f, 1f)), 6f);
            drawList.AddRect(nodeMin, nodeMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.45f, 0.3f, 0.65f, 1f)), 6f);
            drawList.AddText(new System.Numerics.Vector2(nodeMin.X + 8f, nodeMin.Y + 6f), ImGui.GetColorU32(new System.Numerics.Vector4(1f, 1f, 1f, 1f)), $"{groupLabel} {sourceId}");

            var output = new System.Numerics.Vector2(nodeMax.X - 10f, nodeMin.Y + 14f);
            drawList.AddCircleFilled(output, 6f, ImGui.GetColorU32(new System.Numerics.Vector4(0.72f, 0.42f, 1f, 1f)));
            sourcePorts[(sourceKind, sourceId)] = output;

            ImGui.SetCursorScreenPos(new System.Numerics.Vector2(output.X - 8f, output.Y - 8f));
            ImGui.InvisibleButton("mod_src_out", new System.Numerics.Vector2(16f, 16f));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _modGraphLinkStart = (sourceKind, sourceId);
            }

            y += 32f;
            ImGui.PopID();
        }
    }

    private bool TryDisconnectModGraphLinkAtMouse(
        System.Numerics.Vector2 mousePos,
        IReadOnlyList<(bool IsProcessorInput, int ProcessorId, string NodeKey, int SocketId, System.Numerics.Vector2 Start, System.Numerics.Vector2 C1, System.Numerics.Vector2 C2, System.Numerics.Vector2 End)> links)
    {
        (bool IsProcessorInput, int ProcessorId, string NodeKey, int SocketId)? best = null;
        var bestDistance = 12f;

        foreach (var link in links)
        {
            var distance = ModGraphDistancePointToBezierApprox(mousePos, link.Start, link.C1, link.C2, link.End);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = (link.IsProcessorInput, link.ProcessorId, link.NodeKey, link.SocketId);
        }

        if (!best.HasValue)
        {
            return false;
        }

        if (best.Value.IsProcessorInput)
        {
            if (!_modGraphProcessors.TryGetValue(best.Value.ProcessorId, out var processor))
            {
                return false;
            }

            var input = processor.Inputs.FirstOrDefault(x => x.Id == best.Value.SocketId);
            if (input is null)
            {
                return false;
            }

            input.SourceKind = null;
            input.SourceId = null;
            return true;
        }

        if (!_modGraphTargets.TryGetValue(best.Value.NodeKey, out var node))
        {
            return false;
        }

        var socket = node.Sockets.FirstOrDefault(x => x.Id == best.Value.SocketId);
        if (socket is null)
        {
            return false;
        }

        socket.SourceKind = null;
        socket.SourceId = null;
        return true;
    }

    private static float ModGraphDistancePointToBezierApprox(
        System.Numerics.Vector2 point,
        System.Numerics.Vector2 p0,
        System.Numerics.Vector2 p1,
        System.Numerics.Vector2 p2,
        System.Numerics.Vector2 p3)
    {
        const int segments = 24;
        var previous = p0;
        var minDistance = float.MaxValue;

        for (var i = 1; i <= segments; i++)
        {
            var t = i / (float)segments;
            var current = ModGraphCubicBezierPoint(p0, p1, p2, p3, t);
            var distance = ModGraphDistancePointToSegment(point, previous, current);
            if (distance < minDistance)
            {
                minDistance = distance;
            }

            previous = current;
        }

        return minDistance;
    }

    private static System.Numerics.Vector2 ModGraphCubicBezierPoint(
        System.Numerics.Vector2 p0,
        System.Numerics.Vector2 p1,
        System.Numerics.Vector2 p2,
        System.Numerics.Vector2 p3,
        float t)
    {
        var u = 1f - t;
        var tt = t * t;
        var uu = u * u;
        var uuu = uu * u;
        var ttt = tt * t;

        return (uuu * p0) + (3f * uu * t * p1) + (3f * u * tt * p2) + (ttt * p3);
    }

    private static float ModGraphDistancePointToSegment(System.Numerics.Vector2 point, System.Numerics.Vector2 a, System.Numerics.Vector2 b)
    {
        var ab = b - a;
        var abLenSq = (ab.X * ab.X) + (ab.Y * ab.Y);
        if (abLenSq <= 1e-6f)
        {
            return (point - a).Length();
        }

        var ap = point - a;
        var t = ((ap.X * ab.X) + (ap.Y * ab.Y)) / abLenSq;
        t = Math.Clamp(t, 0f, 1f);
        var closest = a + (ab * t);
        return (point - closest).Length();
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

        ImGui.SetNextWindowPos(new System.Numerics.Vector2(920f, 140f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(420f, 0f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Mod Link Inspector", ImGuiWindowFlags.AlwaysAutoResize))
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
                ImGui.PushID(i);
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

                ImGui.PopID();
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

        SanitizeModGraphProcessors();

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

                if (socket.SourceKind == ModGraphSourceKind.Processor && socket.SourceId.HasValue && !_modGraphProcessors.ContainsKey(socket.SourceId.Value))
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

    private void EnsureProcessorInputLayout(ModGraphProcessorNode node)
    {
        switch (node.Kind)
        {
            case ModGraphProcessorKind.Constant:
                node.Inputs.Clear();
                break;
            case ModGraphProcessorKind.Invert:
            case ModGraphProcessorKind.Absolute:
                if (node.Inputs.Count == 0)
                {
                    node.Inputs.Add(NewModGraphSocket());
                }
                else if (node.Inputs.Count > 1)
                {
                    node.Inputs.RemoveRange(1, node.Inputs.Count - 1);
                }
                break;
            case ModGraphProcessorKind.Multiply:
            case ModGraphProcessorKind.Add:
                while (node.Inputs.Count < 2)
                {
                    node.Inputs.Add(NewModGraphSocket());
                }
                break;
        }
    }

    private void AddModGraphProcessorNode(ModGraphProcessorKind kind)
    {
        var id = _nextModGraphProcessorId++;
        var node = new ModGraphProcessorNode
        {
            Id = id,
            Kind = kind,
            Position = new System.Numerics.Vector2(0f, _modGraphProcessors.Count * 120f),
            Inputs = [],
            ConstantValue = 1f
        };

        EnsureProcessorInputLayout(node);
        _modGraphProcessors[id] = node;
    }

    private void RemoveModGraphProcessorNode(int processorId)
    {
        _modGraphProcessors.Remove(processorId);

        foreach (var target in _modGraphTargets.Values)
        {
            foreach (var socket in target.Sockets)
            {
                if (socket.SourceKind == ModGraphSourceKind.Processor && socket.SourceId == processorId)
                {
                    socket.SourceKind = null;
                    socket.SourceId = null;
                }
            }
        }

        foreach (var processor in _modGraphProcessors.Values)
        {
            foreach (var input in processor.Inputs)
            {
                if (input.SourceKind == ModGraphSourceKind.Processor && input.SourceId == processorId)
                {
                    input.SourceKind = null;
                    input.SourceId = null;
                }
            }
        }
    }

    private void SanitizeModGraphProcessors()
    {
        var liveProcessorIds = _modGraphProcessors.Keys.ToHashSet();

        foreach (var processor in _modGraphProcessors.Values)
        {
            EnsureProcessorInputLayout(processor);
            foreach (var input in processor.Inputs)
            {
                if (input.SourceKind == ModGraphSourceKind.Lfo && input.SourceId.HasValue && _lfoEngine.Lfos.All(x => x.Id != input.SourceId.Value))
                {
                    input.SourceKind = null;
                    input.SourceId = null;
                }

                if (input.SourceKind == ModGraphSourceKind.Fft && input.SourceId.HasValue && _fftSources.All(x => x.Id != input.SourceId.Value))
                {
                    input.SourceKind = null;
                    input.SourceId = null;
                }

                if (input.SourceKind == ModGraphSourceKind.Processor && input.SourceId.HasValue)
                {
                    if (!liveProcessorIds.Contains(input.SourceId.Value) || input.SourceId.Value == processor.Id)
                    {
                        input.SourceKind = null;
                        input.SourceId = null;
                    }
                }
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
        _modGraphProcessors.Clear();
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

    private static string ToPresetSourceKind(ModGraphSourceKind kind)
    {
        return kind switch
        {
            ModGraphSourceKind.Lfo => "Lfo",
            ModGraphSourceKind.Fft => "Fft",
            ModGraphSourceKind.Processor => "Processor",
            _ => string.Empty
        };
    }

    private static bool TryParsePresetSourceKind(string value, out ModGraphSourceKind kind)
    {
        kind = value switch
        {
            "Lfo" => ModGraphSourceKind.Lfo,
            "Fft" => ModGraphSourceKind.Fft,
            "Processor" => ModGraphSourceKind.Processor,
            _ => default
        };

        return value is "Lfo" or "Fft" or "Processor";
    }

    private static string ToPresetProcessorKind(ModGraphProcessorKind kind)
    {
        return kind.ToString();
    }

    private static bool TryParsePresetProcessorKind(string value, out ModGraphProcessorKind kind)
    {
        return Enum.TryParse(value, true, out kind);
    }

    private static ModGraphSocketLinkDto CaptureModGraphSocketLink(ModGraphSocket socket)
    {
        return new ModGraphSocketLinkDto
        {
            SourceKind = socket.SourceKind.HasValue ? ToPresetSourceKind(socket.SourceKind.Value) : string.Empty,
            SourceId = socket.SourceId ?? 0,
            LfoScale = socket.Lfo.Scale,
            LfoOffset = socket.Lfo.Offset,
            FftBinIndex = socket.Fft.AudioBinIndex,
            FftScale = socket.Fft.Scale,
            FftOffset = socket.Fft.Offset
        };
    }

    private void ApplyPresetSocketLink(ModGraphSocket socket, ModGraphSocketLinkDto link, IReadOnlyDictionary<int, int> lfoIdMap, IReadOnlyDictionary<int, int> fftIdMap, IReadOnlyDictionary<int, int> processorIdMap)
    {
        socket.SourceKind = null;
        socket.SourceId = null;

        socket.Lfo.Scale = link.LfoScale;
        socket.Lfo.Offset = link.LfoOffset;
        socket.Fft.AudioBinIndex = Math.Max(0, link.FftBinIndex);
        socket.Fft.Scale = link.FftScale;
        socket.Fft.Offset = link.FftOffset;

        if (!TryParsePresetSourceKind(link.SourceKind, out var sourceKind))
        {
            return;
        }

        int mappedId;
        switch (sourceKind)
        {
            case ModGraphSourceKind.Lfo:
                if (!lfoIdMap.TryGetValue(link.SourceId, out mappedId))
                {
                    return;
                }

                break;
            case ModGraphSourceKind.Fft:
                if (!fftIdMap.TryGetValue(link.SourceId, out mappedId))
                {
                    return;
                }

                break;
            case ModGraphSourceKind.Processor:
                if (!processorIdMap.TryGetValue(link.SourceId, out mappedId))
                {
                    return;
                }

                break;
            default:
                return;
        }

        socket.SourceKind = sourceKind;
        socket.SourceId = mappedId;
    }

    private void CaptureModGraphStateToPreset(PipelinePresetEntry entry, VisualPipeline visualPipeline)
    {
        EnsureModGraphTargets(visualPipeline);

        entry.ModGraphNodePositions.Clear();
        entry.ModGraphProcessors.Clear();
        entry.ModGraphTargetLinks.Clear();

        foreach (var target in _modGraphTargets.Values.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            entry.ModGraphNodePositions.Add(new ModGraphNodePositionDto
            {
                NodeKey = target.Key,
                X = target.Position.X,
                Y = target.Position.Y
            });

            foreach (var socket in target.Sockets)
            {
                if (!socket.IsConnected || socket.Parameter is null || !socket.SourceKind.HasValue || !socket.SourceId.HasValue)
                {
                    continue;
                }

                var parameterIndex = target.Parameters.FindIndex(p => ReferenceEquals(p, socket.Parameter));
                entry.ModGraphTargetLinks.Add(new ModGraphTargetLinkDto
                {
                    NodeKey = target.Key,
                    ParameterIndex = parameterIndex,
                    ParameterName = socket.Parameter.Name,
                    Link = CaptureModGraphSocketLink(socket)
                });
            }
        }

        foreach (var processor in _modGraphProcessors.Values.OrderBy(x => x.Id))
        {
            var dto = new ModGraphProcessorNodeDto
            {
                ProcessorId = processor.Id,
                Kind = ToPresetProcessorKind(processor.Kind),
                X = processor.Position.X,
                Y = processor.Position.Y,
                ConstantValue = processor.ConstantValue
            };

            for (var i = 0; i < processor.Inputs.Count; i++)
            {
                var input = processor.Inputs[i];
                dto.Inputs.Add(input.IsConnected && input.SourceKind.HasValue && input.SourceId.HasValue
                    ? CaptureModGraphSocketLink(input)
                    : new ModGraphSocketLinkDto());
            }

            entry.ModGraphProcessors.Add(dto);
        }
    }

    private void ApplyModGraphStateFromPreset(PipelinePresetEntry preset, VisualPipeline visualPipeline, IReadOnlyDictionary<int, int> lfoIdMap, IReadOnlyDictionary<int, int> fftIdMap)
    {
        _modGraphProcessors.Clear();
        _nextModGraphProcessorId = 1;
        EnsureModGraphTargets(visualPipeline);

        foreach (var node in _modGraphTargets.Values)
        {
            node.Sockets.Clear();
            node.Sockets.Add(NewModGraphSocket());
        }

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

        var processorIdMap = new Dictionary<int, int>();
        foreach (var processorDto in preset.ModGraphProcessors)
        {
            if (!TryParsePresetProcessorKind(processorDto.Kind, out var kind))
            {
                continue;
            }

            var id = _nextModGraphProcessorId++;
            var processor = new ModGraphProcessorNode
            {
                Id = id,
                Kind = kind,
                Position = new System.Numerics.Vector2(processorDto.X, processorDto.Y),
                ConstantValue = processorDto.ConstantValue,
                Inputs = []
            };

            EnsureProcessorInputLayout(processor);
            if (processor.Kind is ModGraphProcessorKind.Add or ModGraphProcessorKind.Multiply)
            {
                while (processor.Inputs.Count < processorDto.Inputs.Count)
                {
                    processor.Inputs.Add(NewModGraphSocket());
                }
            }

            _modGraphProcessors[id] = processor;
            processorIdMap[processorDto.ProcessorId] = id;
        }

        foreach (var processorDto in preset.ModGraphProcessors)
        {
            if (!processorIdMap.TryGetValue(processorDto.ProcessorId, out var mappedProcessorId) ||
                !_modGraphProcessors.TryGetValue(mappedProcessorId, out var processor))
            {
                continue;
            }

            for (var i = 0; i < processorDto.Inputs.Count && i < processor.Inputs.Count; i++)
            {
                ApplyPresetSocketLink(processor.Inputs[i], processorDto.Inputs[i], lfoIdMap, fftIdMap, processorIdMap);
            }
        }

        foreach (var targetLink in preset.ModGraphTargetLinks)
        {
            if (!_modGraphTargets.TryGetValue(targetLink.NodeKey, out var target))
            {
                continue;
            }

            var parameter = ParameterUiHelpers.ResolvePipelineParameter(target.Parameters, targetLink.ParameterIndex, targetLink.ParameterName);
            if (parameter is null)
            {
                continue;
            }

            var socket = NewModGraphSocket();
            socket.Parameter = parameter;
            ApplyPresetSocketLink(socket, targetLink.Link, lfoIdMap, fftIdMap, processorIdMap);

            if (socket.IsConnected)
            {
                target.Sockets.Insert(Math.Max(0, target.Sockets.Count - 1), socket);
            }
        }

        SanitizeModGraphProcessors();
        foreach (var target in _modGraphTargets.Values)
        {
            EnsureSocketLayout(target);
        }
    }

    private float GetModGraphProcessorContribution(IParameter parameter, out bool hasProcessorContribution)
    {
        hasProcessorContribution = false;
        var sum = 0f;

        foreach (var target in _modGraphTargets.Values)
        {
            foreach (var socket in target.Sockets)
            {
                if (socket.Parameter is null || !ReferenceEquals(socket.Parameter, parameter) || !socket.IsConnected)
                {
                    continue;
                }

                if (socket.SourceKind != ModGraphSourceKind.Processor || !socket.SourceId.HasValue)
                {
                    continue;
                }

                sum += EvaluateModGraphProcessorOutput(socket.SourceId.Value, new HashSet<int>());
                hasProcessorContribution = true;
            }
        }

        return sum;
    }

    private float EvaluateModGraphSignal(ModGraphSocket socket, HashSet<int> evaluatingProcessors)
    {
        if (!socket.SourceKind.HasValue || !socket.SourceId.HasValue)
        {
            return 0f;
        }

        return socket.SourceKind.Value switch
        {
            ModGraphSourceKind.Lfo => _lfoEngine.TryGetOutput(socket.SourceId.Value, out var lfoValue)
                ? (lfoValue * socket.Lfo.Scale) + socket.Lfo.Offset
                : 0f,
            ModGraphSourceKind.Fft => (GetAudioBinValue(socket.SourceId.Value, socket.Fft.AudioBinIndex) * socket.Fft.Scale) + socket.Fft.Offset,
            ModGraphSourceKind.Processor => EvaluateModGraphProcessorOutput(socket.SourceId.Value, evaluatingProcessors),
            _ => 0f
        };
    }

    private float EvaluateModGraphProcessorOutput(int processorId, HashSet<int> evaluatingProcessors)
    {
        if (!_modGraphProcessors.TryGetValue(processorId, out var processor))
        {
            return 0f;
        }

        if (!evaluatingProcessors.Add(processorId))
        {
            return 0f;
        }

        float result;
        switch (processor.Kind)
        {
            case ModGraphProcessorKind.Constant:
                result = processor.ConstantValue;
                break;
            case ModGraphProcessorKind.Invert:
            {
                var input = processor.Inputs.FirstOrDefault(x => x.IsConnected);
                result = input is null ? 0f : -EvaluateModGraphSignal(input, evaluatingProcessors);
                break;
            }
            case ModGraphProcessorKind.Absolute:
            {
                var input = processor.Inputs.FirstOrDefault(x => x.IsConnected);
                result = input is null ? 0f : MathF.Abs(EvaluateModGraphSignal(input, evaluatingProcessors));
                break;
            }
            case ModGraphProcessorKind.Multiply:
            {
                var connected = processor.Inputs.Where(x => x.IsConnected).ToList();
                if (connected.Count == 0)
                {
                    result = 0f;
                }
                else
                {
                    result = 1f;
                    foreach (var input in connected)
                    {
                        result *= EvaluateModGraphSignal(input, evaluatingProcessors);
                    }
                }

                break;
            }
            case ModGraphProcessorKind.Add:
            {
                result = 0f;
                foreach (var input in processor.Inputs.Where(x => x.IsConnected))
                {
                    result += EvaluateModGraphSignal(input, evaluatingProcessors);
                }

                break;
            }
            default:
                result = 0f;
                break;
        }

        evaluatingProcessors.Remove(processorId);
        return result;
    }
}
