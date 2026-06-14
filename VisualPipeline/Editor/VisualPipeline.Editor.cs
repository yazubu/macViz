using ImGuiNET;

namespace macViz;

public sealed partial class VisualPipeline
{
    public void DrawEditorPanel()
    {
        ImGui.Separator();
        ImGui.Text("Pipeline Graph Editor");
        ImGui.TextDisabled("Drag boxes. Click output port then input port to connect. Pan: right/middle drag or Space+left drag. Zoom: Command +/-.");

        DrawNodeCreationToolbar();

        var canvasSize = new System.Numerics.Vector2(Math.Max(640f, ImGui.GetContentRegionAvail().X), 460f);
        ImGui.BeginChild("PipelineNodeCanvas", canvasSize, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar);
        DrawNodeCanvas(canvasSize);
        ImGui.EndChild();

        DrawSelectedNodeInspector();
    }

    private void DrawNodeCreationToolbar()
    {
        var nodeKindLabels = new[] { "Stage", "Mix Box" };
        var currentKind = nodeKindLabels[Math.Clamp(_newNodeKindIndex, 0, nodeKindLabels.Length - 1)];
        if (ImGui.BeginCombo("New Node Type", currentKind))
        {
            for (var i = 0; i < nodeKindLabels.Length; i++)
            {
                var selected = i == _newNodeKindIndex;
                if (ImGui.Selectable(nodeKindLabels[i], selected))
                {
                    _newNodeKindIndex = i;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (_newNodeKindIndex == 0)
        {
            var currentFactoryLabel = StageFactories[Math.Clamp(_newStageTypeIndex, 0, StageFactories.Length - 1)].Label;
            if (ImGui.BeginCombo("Stage Type", currentFactoryLabel))
            {
                for (var i = 0; i < StageFactories.Length; i++)
                {
                    var selected = i == _newStageTypeIndex;
                    if (ImGui.Selectable(StageFactories[i].Label, selected))
                    {
                        _newStageTypeIndex = i;
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            if (ImGui.Button("Add Stage Node"))
            {
                AddStageNode(StageFactories[_newStageTypeIndex].Create());
            }
        }
        else if (ImGui.Button("Add Mix Box"))
        {
            AddMixNode();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset Default Pipeline"))
        {
            BuildDefaultPipeline();
        }

        ImGui.SameLine();
        if (ImGui.Button("Auto Layout"))
        {
            AutoLayoutNodes();
        }

        if (_linkStartNodeId.HasValue)
        {
            ImGui.SameLine();
            var linkLabel = _linkStartNodeId.Value == CameraVirtualNodeId
                ? "None"
                : GetNodeLabel(_linkStartNodeId.Value);
            ImGui.TextDisabled($"Linking from {linkLabel}...");
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel Link"))
            {
                _linkStartNodeId = null;
            }
        }
    }

    private void DrawNodeCanvas(System.Numerics.Vector2 canvasSize)
    {
        var drawList = ImGui.GetWindowDrawList();
        var canvasPos = ImGui.GetCursorScreenPos();
        var canvasMin = canvasPos;
        var canvasMax = new System.Numerics.Vector2(canvasPos.X + canvasSize.X, canvasPos.Y + canvasSize.Y);

        drawList.AddRectFilled(canvasMin, canvasMax, ImGui.GetColorU32(new System.Numerics.Vector4(0.09f, 0.09f, 0.11f, 1f)));

        var mousePos = ImGui.GetIO().MousePos;
        var canvasHovered =
            ImGui.IsWindowHovered() &&
            mousePos.X >= canvasMin.X && mousePos.X <= canvasMax.X &&
            mousePos.Y >= canvasMin.Y && mousePos.Y <= canvasMax.Y;

        var io = ImGui.GetIO();
        if (canvasHovered && io.KeySuper)
        {
            var zoomInShortcut = ImGui.IsKeyPressed(ImGuiKey.Equal) || ImGui.IsKeyPressed(ImGuiKey.KeypadAdd);
            var zoomOutShortcut = ImGui.IsKeyPressed(ImGuiKey.Minus) || ImGui.IsKeyPressed(ImGuiKey.KeypadSubtract);
            if (zoomInShortcut)
            {
                ApplyCanvasZoom(1.12f, canvasMin, mousePos);
            }
            else if (zoomOutShortcut)
            {
                ApplyCanvasZoom(1f / 1.12f, canvasMin, mousePos);
            }
        }

        DrawCanvasGrid(drawList, canvasMin, canvasMax);

        if (canvasHovered && _linkStartNodeId.HasValue && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _linkStartNodeId = null;
        }

        var isSpaceDown = ImGui.IsKeyDown(ImGuiKey.Space);
        var isPanningCanvas = canvasHovered &&
            (ImGui.IsMouseDragging(ImGuiMouseButton.Right)
             || ImGui.IsMouseDragging(ImGuiMouseButton.Middle)
             || (isSpaceDown && ImGui.IsMouseDragging(ImGuiMouseButton.Left)));

        if (isPanningCanvas)
        {
            var delta = io.MouseDelta;
            _canvasPan.X += delta.X;
            _canvasPan.Y += delta.Y;
        }

        if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyShift && !isSpaceDown)
        {
            _selectedNodeId = null;
        }

        var nodeRects = new Dictionary<int, (System.Numerics.Vector2 Min, System.Numerics.Vector2 Max)>();
        var cameraMin = CanvasToScreen(canvasMin, new System.Numerics.Vector2(-220f, 64f));
        var cameraSize = new System.Numerics.Vector2(150f * _canvasZoom, 76f * _canvasZoom);
        var cameraMax = new System.Numerics.Vector2(cameraMin.X + cameraSize.X, cameraMin.Y + cameraSize.Y);
        nodeRects[CameraVirtualNodeId] = (cameraMin, cameraMax);

        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (node.Position == default)
            {
                node.Position = GetAutoNodePosition(i);
            }

            var baseSize = GetNodeVisualSize(node);
            var size = new System.Numerics.Vector2(baseSize.X * _canvasZoom, baseSize.Y * _canvasZoom);
            var min = CanvasToScreen(canvasMin, node.Position);
            var max = new System.Numerics.Vector2(min.X + size.X, min.Y + size.Y);
            nodeRects[node.Id] = (min, max);
        }

        DrawCameraVirtualNode(drawList, cameraMin, cameraMax);
        DrawNodeConnections(drawList, nodeRects);

        if (canvasHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && TryRemoveConnectionAtMouse(mousePos, nodeRects))
        {
            _linkStartNodeId = null;
        }

        for (var i = 0; i < _nodes.Count; i++)
        {
            DrawNodeWidget(_nodes[i], i, canvasMin, drawList, nodeRects[_nodes[i].Id]);
        }

        if (_linkStartNodeId.HasValue && nodeRects.TryGetValue(_linkStartNodeId.Value, out var startRect))
        {
            var start = GetNodeOutputPort(startRect.Min, startRect.Max);
            var end = ImGui.GetMousePos();
            var c1 = new System.Numerics.Vector2(start.X + 80f, start.Y);
            var c2 = new System.Numerics.Vector2(end.X - 80f, end.Y);
            drawList.AddBezierCubic(start, c1, c2, end, ImGui.GetColorU32(new System.Numerics.Vector4(1f, 0.8f, 0.2f, 1f)), 2.5f);
        }
    }

    private void DrawCameraVirtualNode(ImDrawListPtr drawList, System.Numerics.Vector2 min, System.Numerics.Vector2 max)
    {
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new System.Numerics.Vector4(0.12f, 0.22f, 0.22f, 0.95f)), 6f);
        drawList.AddRect(min, max, ImGui.GetColorU32(new System.Numerics.Vector4(0.25f, 0.85f, 0.9f, 1f)), 6f, ImDrawFlags.None, 1.6f);
        drawList.AddText(new System.Numerics.Vector2(min.X + 10f, min.Y + 10f), ImGui.GetColorU32(new System.Numerics.Vector4(1f, 1f, 1f, 1f)), "None In");
        drawList.AddText(new System.Numerics.Vector2(min.X + 10f, min.Y + 34f), ImGui.GetColorU32(new System.Numerics.Vector4(0.75f, 0.95f, 1f, 1f)), "Virtual null source");

        var output = GetNodeOutputPort(min, max);
        drawList.AddCircleFilled(output, 6f, ImGui.GetColorU32(new System.Numerics.Vector4(0.2f, 0.9f, 1f, 1f)));

        ImGui.SetCursorScreenPos(new System.Numerics.Vector2(output.X - 8f, output.Y - 8f));
        ImGui.InvisibleButton("camera_virtual_out", new System.Numerics.Vector2(16f, 16f));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _linkStartNodeId = CameraVirtualNodeId;
        }
    }

    private void DrawCanvasGrid(ImDrawListPtr drawList, System.Numerics.Vector2 min, System.Numerics.Vector2 max)
    {
        var gridStep = 32f * _canvasZoom;
        if (gridStep < 8f)
        {
            return;
        }

        var color = ImGui.GetColorU32(new System.Numerics.Vector4(0.16f, 0.16f, 0.18f, 1f));

        var startX = min.X + (_canvasPan.X % gridStep);
        for (var x = startX; x < max.X; x += gridStep)
        {
            drawList.AddLine(new System.Numerics.Vector2(x, min.Y), new System.Numerics.Vector2(x, max.Y), color);
        }

        var startY = min.Y + (_canvasPan.Y % gridStep);
        for (var y = startY; y < max.Y; y += gridStep)
        {
            drawList.AddLine(new System.Numerics.Vector2(min.X, y), new System.Numerics.Vector2(max.X, y), color);
        }
    }

    private System.Numerics.Vector2 CanvasToScreen(System.Numerics.Vector2 canvasMin, System.Numerics.Vector2 worldPosition)
    {
        return new System.Numerics.Vector2(
            canvasMin.X + _canvasPan.X + (worldPosition.X * _canvasZoom),
            canvasMin.Y + _canvasPan.Y + (worldPosition.Y * _canvasZoom));
    }

    private void ApplyCanvasZoom(float factor, System.Numerics.Vector2 canvasMin, System.Numerics.Vector2 pivotScreen)
    {
        var oldZoom = _canvasZoom;
        var newZoom = Math.Clamp(oldZoom * factor, 0.45f, 2.5f);
        if (Math.Abs(newZoom - oldZoom) < 0.0001f)
        {
            return;
        }

        var worldX = (pivotScreen.X - canvasMin.X - _canvasPan.X) / oldZoom;
        var worldY = (pivotScreen.Y - canvasMin.Y - _canvasPan.Y) / oldZoom;

        _canvasZoom = newZoom;
        _canvasPan.X = pivotScreen.X - canvasMin.X - (worldX * _canvasZoom);
        _canvasPan.Y = pivotScreen.Y - canvasMin.Y - (worldY * _canvasZoom);
    }

    private void DrawNodeConnections(ImDrawListPtr drawList, IReadOnlyDictionary<int, (System.Numerics.Vector2 Min, System.Numerics.Vector2 Max)> nodeRects)
    {
        foreach (var node in _nodes)
        {
            if (!nodeRects.TryGetValue(node.Id, out var targetRect))
            {
                continue;
            }

            var slotCount = GetNodeInputSlotCount(node);
            for (var slot = 0; slot < slotCount; slot++)
            {
                var inputId = GetNodeInputIdBySlot(node, slot);
                var inputPort = GetNodeInputPort(node, targetRect.Min, targetRect.Max, slot, slotCount);
                DrawConnectionIntoInputPort(drawList, inputId, nodeRects, inputPort, IsCameraAllowedForInputSlot(node, slot));
            }
        }
    }

    private void DrawConnectionIntoInputPort(ImDrawListPtr drawList, int? sourceNodeId, IReadOnlyDictionary<int, (System.Numerics.Vector2 Min, System.Numerics.Vector2 Max)> nodeRects, System.Numerics.Vector2 inputPort, bool allowCamera)
    {
        if (!sourceNodeId.HasValue)
        {
            if (!allowCamera)
            {
                return;
            }

            var cameraStart = new System.Numerics.Vector2(inputPort.X - 130f, inputPort.Y - 18f);
            drawList.AddLine(cameraStart, inputPort, ImGui.GetColorU32(new System.Numerics.Vector4(0.2f, 0.8f, 1f, 0.8f)), 2f);
            drawList.AddText(new System.Numerics.Vector2(cameraStart.X - 46f, cameraStart.Y - 14f), ImGui.GetColorU32(new System.Numerics.Vector4(0.7f, 0.9f, 1f, 1f)), "NONE");
            return;
        }

        if (!nodeRects.TryGetValue(sourceNodeId.Value, out var sourceRect))
        {
            return;
        }

        var outPort = GetNodeOutputPort(sourceRect.Min, sourceRect.Max);
        var c1 = new System.Numerics.Vector2(outPort.X + 90f, outPort.Y);
        var c2 = new System.Numerics.Vector2(inputPort.X - 90f, inputPort.Y);
        drawList.AddBezierCubic(outPort, c1, c2, inputPort, ImGui.GetColorU32(new System.Numerics.Vector4(0.9f, 0.9f, 0.9f, 0.9f)), 2f);
    }

    private bool TryRemoveConnectionAtMouse(System.Numerics.Vector2 mousePos, IReadOnlyDictionary<int, (System.Numerics.Vector2 Min, System.Numerics.Vector2 Max)> nodeRects)
    {
        PipelineNode? bestNode = null;
        var bestSlot = -1;
        var bestDistance = 12f;

        foreach (var node in _nodes)
        {
            if (!nodeRects.TryGetValue(node.Id, out var targetRect))
            {
                continue;
            }

            var slotCount = GetNodeInputSlotCount(node);
            for (var slot = 0; slot < slotCount; slot++)
            {
                var inputId = GetNodeInputIdBySlot(node, slot);
                var inputPort = GetNodeInputPort(node, targetRect.Min, targetRect.Max, slot, slotCount);
                if (!TryGetConnectionDistanceToPoint(inputId, nodeRects, inputPort, IsCameraAllowedForInputSlot(node, slot), mousePos, out var distance))
                {
                    continue;
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestNode = node;
                    bestSlot = slot;
                }
            }
        }

        if (bestNode is null || bestSlot < 0)
        {
            return false;
        }

        SetNodeInputIdBySlot(bestNode, bestSlot, null);
        SanitizeNodeConnections();
        return true;
    }

    private static bool TryGetConnectionDistanceToPoint(
        int? sourceNodeId,
        IReadOnlyDictionary<int, (System.Numerics.Vector2 Min, System.Numerics.Vector2 Max)> nodeRects,
        System.Numerics.Vector2 inputPort,
        bool allowCamera,
        System.Numerics.Vector2 point,
        out float distance)
    {
        if (!sourceNodeId.HasValue)
        {
            if (!allowCamera)
            {
                distance = float.MaxValue;
                return false;
            }

            var cameraStart = new System.Numerics.Vector2(inputPort.X - 130f, inputPort.Y - 18f);
            distance = DistancePointToSegment(point, cameraStart, inputPort);
            return true;
        }

        if (!nodeRects.TryGetValue(sourceNodeId.Value, out var sourceRect))
        {
            distance = float.MaxValue;
            return false;
        }

        var outPort = GetNodeOutputPort(sourceRect.Min, sourceRect.Max);
        var c1 = new System.Numerics.Vector2(outPort.X + 90f, outPort.Y);
        var c2 = new System.Numerics.Vector2(inputPort.X - 90f, inputPort.Y);
        distance = DistancePointToBezierApprox(point, outPort, c1, c2, inputPort);
        return true;
    }

    private static float DistancePointToBezierApprox(System.Numerics.Vector2 point, System.Numerics.Vector2 p0, System.Numerics.Vector2 p1, System.Numerics.Vector2 p2, System.Numerics.Vector2 p3)
    {
        const int segments = 24;
        var previous = p0;
        var minDistance = float.MaxValue;

        for (var i = 1; i <= segments; i++)
        {
            var t = i / (float)segments;
            var current = CubicBezierPoint(p0, p1, p2, p3, t);
            var distance = DistancePointToSegment(point, previous, current);
            if (distance < minDistance)
            {
                minDistance = distance;
            }

            previous = current;
        }

        return minDistance;
    }

    private static System.Numerics.Vector2 CubicBezierPoint(System.Numerics.Vector2 p0, System.Numerics.Vector2 p1, System.Numerics.Vector2 p2, System.Numerics.Vector2 p3, float t)
    {
        var oneMinusT = 1f - t;
        return (oneMinusT * oneMinusT * oneMinusT * p0)
             + (3f * oneMinusT * oneMinusT * t * p1)
             + (3f * oneMinusT * t * t * p2)
             + (t * t * t * p3);
    }

    private static float DistancePointToSegment(System.Numerics.Vector2 point, System.Numerics.Vector2 a, System.Numerics.Vector2 b)
    {
        var ab = b - a;
        var abLengthSquared = System.Numerics.Vector2.Dot(ab, ab);
        if (abLengthSquared <= float.Epsilon)
        {
            return (point - a).Length();
        }

        var t = System.Numerics.Vector2.Dot(point - a, ab) / abLengthSquared;
        t = Math.Clamp(t, 0f, 1f);
        var projection = a + (ab * t);
        return (point - projection).Length();
    }

    private void DrawNodeWidget(PipelineNode node, int nodeIndex, System.Numerics.Vector2 canvasMin, ImDrawListPtr drawList, (System.Numerics.Vector2 Min, System.Numerics.Vector2 Max) rect)
    {
        var min = rect.Min;
        var max = rect.Max;
        var isSelected = _selectedNodeId == node.Id;
        var fill = node.Kind switch
        {
            PipelineNodeKind.Stage => new System.Numerics.Vector4(0.16f, 0.18f, 0.24f, 0.98f),
            PipelineNodeKind.Mix => new System.Numerics.Vector4(0.20f, 0.16f, 0.24f, 0.98f),
            _ => new System.Numerics.Vector4(0.18f, 0.20f, 0.16f, 0.98f)
        };

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), 6f);
        drawList.AddRect(min, max, ImGui.GetColorU32(isSelected ? new System.Numerics.Vector4(1f, 0.9f, 0.3f, 1f) : new System.Numerics.Vector4(0.45f, 0.5f, 0.6f, 1f)), 6f, ImDrawFlags.None, isSelected ? 2.5f : 1.2f);
        drawList.AddText(new System.Numerics.Vector2(min.X + 10f, min.Y + 8f), ImGui.GetColorU32(new System.Numerics.Vector4(1f, 1f, 1f, 1f)), GetNodeLabel(node));

        if (node.Stage is CameraSourceStage cameraStage && cameraStage.HasDeviceWarning)
        {
            drawList.AddText(
                new System.Numerics.Vector2(max.X - 18f, min.Y + 8f),
                ImGui.GetColorU32(new System.Numerics.Vector4(1f, 0.75f, 0.2f, 1f)),
                "!");
        }

        var details = node.Kind switch
        {
            PipelineNodeKind.Stage when node.Stage is SignalSwitchStage => "Switch\nIn: 8 Out: 1",
            PipelineNodeKind.Stage when node.Stage is not null && node.Stage.IsSourceStage => "Source\nOut: 1",
            PipelineNodeKind.Stage => "Effect\nIn: 1 Out: 1",
            PipelineNodeKind.Mix => "Mix\nIn: 2 Out: 1",
            _ => "Output\nIn: 1"
        };
        drawList.AddText(new System.Numerics.Vector2(min.X + 10f, min.Y + 34f), ImGui.GetColorU32(new System.Numerics.Vector4(0.78f, 0.82f, 0.9f, 1f)), details);

        ImGui.SetCursorScreenPos(min);
        var size = new System.Numerics.Vector2(max.X - min.X, max.Y - min.Y);
        ImGui.InvisibleButton($"node_drag_{node.Id}", size);
        var isSpaceDown = ImGui.IsKeyDown(ImGuiKey.Space);
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && !isSpaceDown)
        {
            var delta = ImGui.GetIO().MouseDelta;
            node.Position.X += delta.X / _canvasZoom;
            node.Position.Y += delta.Y / _canvasZoom;
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !isSpaceDown)
        {
            _selectedNodeId = node.Id;
            if (_linkStartNodeId.HasValue && TryAutoLinkNodeOnSelection(node))
            {
                _linkStartNodeId = null;
            }
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && node.Kind != PipelineNodeKind.Output)
        {
            _selectedNodeId = node.Id;
            ImGui.OpenPopup($"node_ctx_{node.Id}");
        }

        if (ImGui.BeginPopup($"node_ctx_{node.Id}"))
        {
            if (ImGui.MenuItem("Delete Node"))
            {
                RemoveNodeAt(nodeIndex);
                ImGui.EndPopup();
                return;
            }

            ImGui.EndPopup();
        }

        var slotCount = GetNodeInputSlotCount(node);
        for (var slot = 0; slot < slotCount; slot++)
        {
            var inputPort = GetNodeInputPort(node, min, max, slot, slotCount);
            DrawInputPort(drawList, inputPort, node, slot, IsCameraAllowedForInputSlot(node, slot));
        }

        if (node.Kind != PipelineNodeKind.Output)
        {
            var output = GetNodeOutputPort(min, max);
            DrawOutputPort(drawList, output, node);
        }
    }

    private void DrawInputPort(ImDrawListPtr drawList, System.Numerics.Vector2 portPos, PipelineNode node, int slotIndex, bool allowCamera)
    {
        drawList.AddCircleFilled(portPos, 6f, ImGui.GetColorU32(new System.Numerics.Vector4(0.25f, 0.85f, 1f, 1f)));
        ImGui.SetCursorScreenPos(new System.Numerics.Vector2(portPos.X - 8f, portPos.Y - 8f));
        ImGui.InvisibleButton($"port_in_{node.Id}_{slotIndex}", new System.Numerics.Vector2(16f, 16f));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && _linkStartNodeId.HasValue)
        {
            var sourceId = _linkStartNodeId.Value;
            _linkStartNodeId = null;
            if (sourceId == node.Id)
            {
                return;
            }

            int? assignment = sourceId;
            if (sourceId == CameraVirtualNodeId)
            {
                if (!allowCamera)
                {
                    return;
                }

                assignment = null;
            }
            else if (!CanCreateConnection(sourceId, node.Id))
            {
                return;
            }

            SetNodeInputIdBySlot(node, slotIndex, assignment);
            SanitizeNodeConnections();
        }
    }

    private bool TryAutoLinkNodeOnSelection(PipelineNode targetNode)
    {
        if (!_linkStartNodeId.HasValue || _linkStartNodeId.Value == targetNode.Id)
        {
            return false;
        }

        if (targetNode.Kind == PipelineNodeKind.Stage && targetNode.Stage is not null && targetNode.Stage.IsSourceStage)
        {
            return false;
        }

        int? assignment;
        var sourceId = _linkStartNodeId.Value;
        if (sourceId == CameraVirtualNodeId)
        {
            if (GetNodeInputSlotCount(targetNode) == 0)
            {
                return false;
            }

            assignment = null;
        }
        else
        {
            if (!CanCreateConnection(sourceId, targetNode.Id))
            {
                return false;
            }

            assignment = sourceId;
        }

        var assigned = false;
        var slotCount = GetNodeInputSlotCount(targetNode);
        for (var slot = 0; slot < slotCount; slot++)
        {
            if (!IsCameraAllowedForInputSlot(targetNode, slot) && !assignment.HasValue)
            {
                continue;
            }

            if (GetNodeInputIdBySlot(targetNode, slot).HasValue)
            {
                continue;
            }

            SetNodeInputIdBySlot(targetNode, slot, assignment);
            assigned = true;
            break;
        }

        if (!assigned)
        {
            return false;
        }

        SanitizeNodeConnections();
        return true;
    }

    private bool CanCreateConnection(int sourceId, int targetId)
    {
        if (sourceId == targetId)
        {
            return false;
        }

        if (_nodes.All(x => x.Id != sourceId) || _nodes.All(x => x.Id != targetId))
        {
            return false;
        }

        return !WouldCreateCycle(sourceId, targetId);
    }

    private bool WouldCreateCycle(int sourceId, int targetId)
    {
        var stack = new Stack<int>();
        var visited = new HashSet<int>();
        stack.Push(targetId);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == sourceId)
            {
                return true;
            }

            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var candidate in _nodes)
            {
                if (candidate.InputAId == current || candidate.InputBId == current || candidate.InputExtraIds.Any(x => x == current))
                {
                    stack.Push(candidate.Id);
                }
            }
        }

        return false;
    }

    private void DrawOutputPort(ImDrawListPtr drawList, System.Numerics.Vector2 portPos, PipelineNode node)
    {
        var color = _linkStartNodeId == node.Id
            ? new System.Numerics.Vector4(1f, 0.8f, 0.2f, 1f)
            : new System.Numerics.Vector4(1f, 0.45f, 0.25f, 1f);

        drawList.AddCircleFilled(portPos, 6f, ImGui.GetColorU32(color));
        ImGui.SetCursorScreenPos(new System.Numerics.Vector2(portPos.X - 8f, portPos.Y - 8f));
        ImGui.InvisibleButton($"port_out_{node.Id}", new System.Numerics.Vector2(16f, 16f));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _linkStartNodeId = node.Id;
        }
    }

    private void DrawSelectedNodeInspector()
    {
        var selected = _selectedNodeId.HasValue ? _nodes.FirstOrDefault(x => x.Id == _selectedNodeId.Value) : null;
        if (selected is null)
        {
            ImGui.TextDisabled("Select a node to edit connections and actions.");
            return;
        }

        ImGui.Separator();
        ImGui.Text($"Selected: {GetNodeLabel(selected)}");

        var selectedIndex = _nodes.FindIndex(x => x.Id == selected.Id);
        if (selected.Kind == PipelineNodeKind.Stage && selected.Stage is not null)
        {
            if (selected.Stage is SignalSwitchStage)
            {
                for (var slot = 0; slot < SignalSwitchStage.MaxInputs; slot++)
                {
                    var inputId = GetNodeInputIdBySlot(selected, slot);
                    DrawInputNodeSelector($"Input {slot + 1}", selectedIndex, ref inputId, allowCamera: false);
                    SetNodeInputIdBySlot(selected, slot, inputId);
                }
            }
            else if (!selected.Stage.IsSourceStage)
            {
                DrawInputNodeSelector("Input", selectedIndex, ref selected.InputAId, allowCamera: true);
            }
            else
            {
                ImGui.TextDisabled("Source node: no input");
            }

            if (selected.Stage is CameraSourceStage cameraSourceStage)
            {
                DrawCameraSourceInspector(selected.Id, cameraSourceStage);
            }
            else if (selected.Stage is StaticImageSourceStage staticImageSourceStage)
            {
                DrawStaticImageSourceInspector(selected.Id, staticImageSourceStage);
            }
        }
        else if (selected.Kind == PipelineNodeKind.Mix)
        {
            DrawInputNodeSelector("Input A", selectedIndex, ref selected.InputAId, allowCamera: false);
            DrawInputNodeSelector("Input B", selectedIndex, ref selected.InputBId, allowCamera: false);
        }
        else
        {
            DrawInputNodeSelector("Final Input", selectedIndex, ref selected.InputAId, allowCamera: true);
        }

        if (selected.Kind != PipelineNodeKind.Output && ImGui.Button("Delete Selected Node"))
        {
            RemoveNodeAt(selectedIndex);
            _selectedNodeId = null;
        }
    }

    private void DrawCameraSourceInspector(int nodeId, CameraSourceStage cameraSourceStage)
    {
        ImGui.Separator();
        ImGui.PushID($"camera_source_inspector_{nodeId}");
        ImGui.Text("Camera Source");

        if (ImGui.Button("Refresh Devices"))
        {
            cameraSourceStage.RefreshDevices();
        }

        if (cameraSourceStage.AvailableDeviceIndices.Count == 0)
        {
            ImGui.TextDisabled("No devices found");
        }
        else
        {
            var selectedLabel = $"Device {cameraSourceStage.SelectedDeviceIndex}";
            if (ImGui.BeginCombo("Device", selectedLabel))
            {
                foreach (var deviceIndex in cameraSourceStage.AvailableDeviceIndices)
                {
                    var selected = deviceIndex == cameraSourceStage.SelectedDeviceIndex;
                    if (ImGui.Selectable($"Device {deviceIndex}", selected))
                    {
                        cameraSourceStage.SetSelectedDeviceIndex(deviceIndex);
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }
        }

        ImGui.TextDisabled(cameraSourceStage.CameraStatus);
        if (cameraSourceStage.HasDeviceWarning)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.2f, 1f), $"! {cameraSourceStage.DeviceWarning}");
        }

        ImGui.PopID();
    }

    private void DrawStaticImageSourceInspector(int nodeId, StaticImageSourceStage staticImageSourceStage)
    {
        ImGui.Separator();
        ImGui.PushID($"static_image_source_inspector_{nodeId}");
        ImGui.Text("Static Images Source");

        var draftPath = _staticImagePathDraftByNode.TryGetValue(nodeId, out var currentDraft) ? currentDraft : string.Empty;
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Path (file or folder)", ref draftPath, 1024);
        _staticImagePathDraftByNode[nodeId] = draftPath;

        if (ImGui.Button("Add"))
        {
            var added = 0;
            var pathCandidates = draftPath
                .Split(['\n', '\r', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (pathCandidates.Length == 0)
            {
                added += staticImageSourceStage.AddImagesFromPath(draftPath);
            }
            else
            {
                foreach (var pathCandidate in pathCandidates)
                {
                    added += staticImageSourceStage.AddImagesFromPath(pathCandidate);
                }
            }

            if (added > 0)
            {
                _staticImagePathDraftByNode[nodeId] = string.Empty;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Pick Files…"))
        {
            var files = NativeFilePicker.PickImageFiles();
            var added = 0;
            foreach (var file in files)
            {
                added += staticImageSourceStage.AddImagesFromPath(file);
            }

            if (added > 0)
            {
                _staticImagePathDraftByNode[nodeId] = string.Empty;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Pick Folder…"))
        {
            var folder = NativeFilePicker.PickFolder();
            if (!string.IsNullOrWhiteSpace(folder))
            {
                staticImageSourceStage.AddImagesFromPath(folder);
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear All"))
        {
            staticImageSourceStage.ClearImages();
        }

        ImGui.Text($"Loaded: {staticImageSourceStage.ImagePaths.Count}");
        ImGui.TextDisabled(staticImageSourceStage.Status);

        var selectedImageIndex = staticImageSourceStage.SelectedImageIndex;
        if (ImGui.BeginListBox("##static_images", new System.Numerics.Vector2(-1f, 130f)))
        {
            for (var i = 0; i < staticImageSourceStage.ImagePaths.Count; i++)
            {
                var imagePath = staticImageSourceStage.ImagePaths[i];
                var isSelected = i == selectedImageIndex;
                if (ImGui.Selectable($"{i + 1}. {Path.GetFileName(imagePath)}", isSelected))
                {
                    staticImageSourceStage.SetSelectedImageIndex(i);
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndListBox();
        }

        if (selectedImageIndex >= 0 && selectedImageIndex < staticImageSourceStage.ImagePaths.Count)
        {
            ImGui.TextWrapped(staticImageSourceStage.ImagePaths[selectedImageIndex]);
            if (ImGui.Button("Remove Selected"))
            {
                staticImageSourceStage.RemoveImageAt(selectedImageIndex);
            }
        }

        ImGui.PopID();
    }

    private static System.Numerics.Vector2 GetNodeVisualSize(PipelineNode node)
    {
        return node.Kind switch
        {
            PipelineNodeKind.Output => new System.Numerics.Vector2(220f, 96f),
            PipelineNodeKind.Mix => new System.Numerics.Vector2(240f, 120f),
            PipelineNodeKind.Stage when node.Stage is SignalSwitchStage => new System.Numerics.Vector2(260f, 248f),
            _ => new System.Numerics.Vector2(240f, 112f)
        };
    }

    private static System.Numerics.Vector2 GetNodeOutputPort(System.Numerics.Vector2 min, System.Numerics.Vector2 max)
    {
        return new System.Numerics.Vector2(max.X, (min.Y + max.Y) * 0.5f);
    }

    private static int GetNodeInputSlotCount(PipelineNode node)
    {
        if (node.Kind == PipelineNodeKind.Output)
        {
            return 1;
        }

        if (node.Kind == PipelineNodeKind.Mix)
        {
            return 2;
        }

        if (node.Kind == PipelineNodeKind.Stage)
        {
            if (node.Stage is null || node.Stage.IsSourceStage)
            {
                return 0;
            }

            if (node.Stage is SignalSwitchStage)
            {
                return SignalSwitchStage.MaxInputs;
            }

            return 1;
        }

        return 0;
    }

    private static bool IsCameraAllowedForInputSlot(PipelineNode node, int slotIndex)
    {
        return node.Kind switch
        {
            PipelineNodeKind.Mix => false,
            PipelineNodeKind.Stage when node.Stage is SignalSwitchStage => false,
            PipelineNodeKind.Stage when node.Stage is not null && node.Stage.IsSourceStage => false,
            _ => slotIndex == 0
        };
    }

    private static int? GetNodeInputIdBySlot(PipelineNode node, int slotIndex)
    {
        if (slotIndex <= 0)
        {
            return node.InputAId;
        }

        if (slotIndex == 1 && node.Kind == PipelineNodeKind.Mix)
        {
            return node.InputBId;
        }

        var extraIndex = slotIndex - 1;
        if (extraIndex >= 0 && extraIndex < node.InputExtraIds.Count)
        {
            return node.InputExtraIds[extraIndex];
        }

        return null;
    }

    private static void SetNodeInputIdBySlot(PipelineNode node, int slotIndex, int? value)
    {
        if (slotIndex <= 0)
        {
            node.InputAId = value;
            return;
        }

        if (slotIndex == 1 && node.Kind == PipelineNodeKind.Mix)
        {
            node.InputBId = value;
            return;
        }

        var extraIndex = slotIndex - 1;
        while (node.InputExtraIds.Count <= extraIndex)
        {
            node.InputExtraIds.Add(null);
        }

        node.InputExtraIds[extraIndex] = value;
    }

    private static System.Numerics.Vector2 GetNodeInputPort(PipelineNode node, System.Numerics.Vector2 min, System.Numerics.Vector2 max, int slotIndex, int slotCount)
    {
        if (slotCount <= 1)
        {
            return new System.Numerics.Vector2(min.X, (min.Y + max.Y) * 0.5f);
        }

        var top = min.Y + 30f;
        var bottom = max.Y - 30f;
        var t = slotIndex / (float)(slotCount - 1);
        var y = top + ((bottom - top) * t);
        return new System.Numerics.Vector2(min.X, y);
    }

    private static System.Numerics.Vector2 GetAutoNodePosition(int index)
    {
        const float columnWidth = 310f;
        const float rowHeight = 170f;
        var col = index % 3;
        var row = index / 3;
        return new System.Numerics.Vector2(30f + (col * columnWidth), 24f + (row * rowHeight));
    }

    private void AutoLayoutNodes()
    {
        var renderable = _nodes.Where(x => x.Kind != PipelineNodeKind.Output).ToList();
        for (var i = 0; i < renderable.Count; i++)
        {
            renderable[i].Position = GetAutoNodePosition(i);
        }

        var output = _nodes.FirstOrDefault(x => x.Kind == PipelineNodeKind.Output);
        if (output is not null)
        {
            output.Position = GetAutoNodePosition(Math.Max(0, renderable.Count));
        }
    }

}
