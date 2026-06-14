using ImGuiNET;
using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline : IVisual, ICameraVisual, IVisualEditorPanel
{
    private const int MaxSnapshots = 8;
    private const int CameraVirtualNodeId = 0;

    private readonly List<PipelineNode> _nodes = [];
    private readonly Queue<PipelineStage> _pendingDisposals = new();
    private readonly List<IParameter> _parameters = [];
    private readonly Dictionary<int, int> _nodeOutputTextures = [];

    private int _nextNodeId = 1;

    private CameraInput? _cameraInput;
    private List<int> _deviceIndices = [];
    private int _selectedDeviceIndex;
    private string _cameraStatus = "Not initialized";
    private bool _cameraReinitPending;

    private int _quadVao;
    private int _quadVbo;
    private int _quadEbo;

    private int _blitProgram;
    private int _blitProgramFlipY;
    private int _blendProgram;
    private int _uBlendBaseTexture;
    private int _uBlendLayerTexture;
    private int _uBlendMix;
    private int _uBlendMode;

    private int _stageTexture;
    private int _stageFbo;
    private int _copyFboRead;
    private int _copyFboDraw;
    private int _renderWidth;
    private int _renderHeight;

    private int _newStageTypeIndex;
    private int _newNodeKindIndex;
    private int? _selectedNodeId;
    private int? _linkStartNodeId;
    private System.Numerics.Vector2 _canvasPan = new(24f, 24f);

    private static readonly StageFactory[] StageFactories =
    [
        new(CameraSourceStage.TypeIdValue, "Camera Source", () => new CameraSourceStage()),
        new(SourceVisualStage.RotatingCubeTypeId, "Rotating Cube Source", () => new SourceVisualStage("Rotating Cube", new RotatingCube3D(), SourceVisualStage.RotatingCubeTypeId)),
        new(SourceVisualStage.SpectrumBarsTypeId, "Spectrum Bars Source", () => new SourceVisualStage("Spectrum Bars", new SpectrumBars2d(), SourceVisualStage.SpectrumBarsTypeId)),
        new(SourceVisualStage.ParticleSystemTypeId, "Particle System Source", () => new SourceVisualStage("Particle System", new RotatingParticleSystem3D(), SourceVisualStage.ParticleSystemTypeId)),
        new(SourceVisualStage.CymaticSpiralsTypeId, "Cymatic Spirals Source", () => new SourceVisualStage("Cymatic Spirals", new CymaticSpirals3D(), SourceVisualStage.CymaticSpiralsTypeId)),
        new(SourceVisualStage.DiffusionPaintingTypeId, "Diffusion Painting Source", () => new SourceVisualStage("Diffusion Painting", new DiffusionPainting2D(), SourceVisualStage.DiffusionPaintingTypeId)),
        new(EdgeDetectEffectStage.TypeIdValue, "Edge Detection Effect", () => new EdgeDetectEffectStage()),
        new(SnapshotPeakEffectStage.TypeIdValue, "Snapshot Peak Hold Effect", () => new SnapshotPeakEffectStage()),
        new(FrameFreezeEffectStage.TypeIdValue, "Frame Freeze Effect", () => new FrameFreezeEffectStage()),
        new(ScaleEffectStage.TypeIdValue, "Scale Effect", () => new ScaleEffectStage()),
        new(ColorShiftEffectStage.TypeIdValue, "Color Shift Effect", () => new ColorShiftEffectStage()),
        new(PixelateEffectStage.TypeIdValue, "Pixelate Effect", () => new PixelateEffectStage()),
        new(PosterizeEffectStage.TypeIdValue, "Posterize Effect", () => new PosterizeEffectStage()),
        new(RadialWavesEffectStage.TypeIdValue, "Radial Waves Effect", () => new RadialWavesEffectStage()),
        new(RadialBlurEffectStage.TypeIdValue, "Radial Blur Effect", () => new RadialBlurEffectStage()),
        new(ZoomBlurEffectStage.TypeIdValue, "Zoom Blur Effect", () => new ZoomBlurEffectStage()),
        new(MotionBlurEffectStage.TypeIdValue, "Motion Blur Effect", () => new MotionBlurEffectStage()),
        new(MotionIsolateEffectStage.TypeIdValue, "Motion Isolate Effect", () => new MotionIsolateEffectStage()),
        new(ChromaticSmearEffectStage.TypeIdValue, "Chromatic Smear Effect", () => new ChromaticSmearEffectStage()),
        new(CubistEchoesEffectStage.TypeIdValue, "Cubist Echoes Effect", () => new CubistEchoesEffectStage()),
        new(CubistDelayEffectStage.TypeIdValue, "Cubist Delay Effect", () => new CubistDelayEffectStage()),
        new(CodecBleedEffectStage.TypeIdValue, "Codec Bleed Effect", () => new CodecBleedEffectStage()),
        new(ColorSwapEffectStage.TypeIdValue, "Color Swap Effect", () => new ColorSwapEffectStage()),
        new(TypographicMatrixEffectStage.TypeIdValue, "Typographic Matrix Effect", () => new TypographicMatrixEffectStage()),
        new(KaleidoscopeEffectStage.TypeIdValue, "Kaleidoscope Effect", () => new KaleidoscopeEffectStage())
    ];

    public string Name => "Visual Pipeline";
    public IReadOnlyList<IParameter> Parameters
    {
        get
        {
            RefreshDynamicStageParameters();
            return _parameters;
        }
    }

    public bool TryGetStageDescriptorForParameter(IParameter parameter, out int stageNumber, out string stageName)
    {
        stageNumber = 0;
        stageName = string.Empty;

        var stageCounter = 0;
        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (node.Kind != PipelineNodeKind.Stage || node.Stage is null)
            {
                continue;
            }

            stageCounter++;
            foreach (var stageParameter in node.Stage.GetAllParameters())
            {
                if (!ReferenceEquals(stageParameter, parameter))
                {
                    continue;
                }

                stageNumber = stageCounter;
                stageName = node.Stage.Name;
                return true;
            }
        }

        return false;
    }

    public bool TryGetNodeDescriptorForParameter(IParameter parameter, out string nodeLabel)
    {
        var stageCounter = 0;
        var mixCounter = 0;

        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            switch (node.Kind)
            {
                case PipelineNodeKind.Stage when node.Stage is not null:
                {
                    stageCounter++;
                    foreach (var stageParameter in node.Stage.GetAllParameters())
                    {
                        if (!ReferenceEquals(stageParameter, parameter))
                        {
                            continue;
                        }

                        nodeLabel = $"Stage {stageCounter} ({node.Stage.Name})";
                        return true;
                    }

                    break;
                }
                case PipelineNodeKind.Mix when node.MixBox is not null:
                {
                    mixCounter++;
                    foreach (var mixParameter in node.MixBox.GetAllParameters())
                    {
                        if (!ReferenceEquals(mixParameter, parameter))
                        {
                            continue;
                        }

                        nodeLabel = $"Mix {mixCounter}";
                        return true;
                    }

                    break;
                }
            }
        }

        nodeLabel = string.Empty;
        return false;
    }

    public IReadOnlyList<int> AvailableDeviceIndices => _deviceIndices;
    public int SelectedDeviceIndex => _selectedDeviceIndex;
    public string CameraStatus => _cameraStatus;

    public VisualPipeline()
    {
        RefreshDevices();
        CreateGlResources();

        BuildDefaultPipeline();
    }

    public void RefreshDevices()
    {
        _deviceIndices = CameraInput.EnumerateDeviceIndices();

        if (_deviceIndices.Count == 0)
        {
            _selectedDeviceIndex = 0;
            _cameraStatus = "No camera devices found";
            return;
        }

        if (!_deviceIndices.Contains(_selectedDeviceIndex))
        {
            _selectedDeviceIndex = _deviceIndices[0];
            _cameraReinitPending = true;
            _cameraStatus = $"Switching to device {_selectedDeviceIndex}...";
        }
    }

    public void SetSelectedDeviceIndex(int deviceIndex)
    {
        if (_selectedDeviceIndex == deviceIndex)
        {
            return;
        }

        _selectedDeviceIndex = deviceIndex;
        _cameraReinitPending = true;
        _cameraStatus = $"Switching to device {_selectedDeviceIndex}...";
    }

    public VisualPipelinePresetState CapturePresetState()
    {
        RefreshDynamicStageParameters();

        var state = new VisualPipelinePresetState();
        foreach (var node in _nodes)
        {
            var nodeState = new VisualPipelineNodePresetState
            {
                NodeId = node.Id,
                NodeKind = node.Kind.ToString(),
                InputAId = node.InputAId,
                InputBId = node.InputBId,
                StageTypeId = node.Stage?.TypeId,
                PositionX = node.Position.X,
                PositionY = node.Position.Y
            };

            foreach (var parameter in node.GetAllParameters())
            {
                nodeState.ParameterValues[parameter.Name] = GetParameterNumericValue(parameter);
            }

            state.Nodes.Add(nodeState);
        }

        return state;
    }

    public void ApplyPresetState(VisualPipelinePresetState? state)
    {
        if (state is null)
        {
            BuildDefaultPipeline();
            return;
        }

        if (state.Nodes.Count > 0)
        {
            ApplyGraphPresetState(state);
            return;
        }

        if (state.Stages.Count == 0)
        {
            BuildDefaultPipeline();
            return;
        }

        ApplyLegacySequentialPresetState(state.Stages);
    }

    public void DrawEditorPanel()
    {
        ImGui.Separator();
        ImGui.Text("Pipeline Graph Editor");
        ImGui.TextDisabled("Drag boxes. Click output port then input port to connect. Right-click canvas to pan.");

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
                ? "Camera"
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
        DrawCanvasGrid(drawList, canvasMin, canvasMax);

        var mousePos = ImGui.GetIO().MousePos;
        var canvasHovered =
            ImGui.IsWindowHovered() &&
            mousePos.X >= canvasMin.X && mousePos.X <= canvasMax.X &&
            mousePos.Y >= canvasMin.Y && mousePos.Y <= canvasMax.Y;

        if (canvasHovered && _linkStartNodeId.HasValue && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _linkStartNodeId = null;
        }

        if (canvasHovered && ImGui.IsMouseDragging(ImGuiMouseButton.Right))
        {
            var delta = ImGui.GetIO().MouseDelta;
            _canvasPan.X += delta.X;
            _canvasPan.Y += delta.Y;
        }

        if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.GetIO().KeyShift)
        {
            _selectedNodeId = null;
        }

        var nodeRects = new Dictionary<int, (System.Numerics.Vector2 Min, System.Numerics.Vector2 Max)>();
        var cameraMin = new System.Numerics.Vector2(canvasMin.X + _canvasPan.X - 220f, canvasMin.Y + _canvasPan.Y + 64f);
        var cameraMax = new System.Numerics.Vector2(cameraMin.X + 150f, cameraMin.Y + 76f);
        nodeRects[CameraVirtualNodeId] = (cameraMin, cameraMax);

        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (node.Position == default)
            {
                node.Position = GetAutoNodePosition(i);
            }

            var size = GetNodeVisualSize(node);
            var min = new System.Numerics.Vector2(canvasMin.X + _canvasPan.X + node.Position.X, canvasMin.Y + _canvasPan.Y + node.Position.Y);
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
        drawList.AddText(new System.Numerics.Vector2(min.X + 10f, min.Y + 10f), ImGui.GetColorU32(new System.Numerics.Vector4(1f, 1f, 1f, 1f)), "Camera In");
        drawList.AddText(new System.Numerics.Vector2(min.X + 10f, min.Y + 34f), ImGui.GetColorU32(new System.Numerics.Vector4(0.75f, 0.95f, 1f, 1f)), "Virtual source");

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
        const float gridStep = 32f;
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

    private void DrawNodeConnections(ImDrawListPtr drawList, IReadOnlyDictionary<int, (System.Numerics.Vector2 Min, System.Numerics.Vector2 Max)> nodeRects)
    {
        foreach (var node in _nodes)
        {
            if (!nodeRects.TryGetValue(node.Id, out var targetRect))
            {
                continue;
            }

            if (!(node.Kind == PipelineNodeKind.Stage && node.Stage is not null && node.Stage.IsSourceStage))
            {
                DrawConnectionIntoInputPort(drawList, node.InputAId, nodeRects, GetNodeInputPortA(node, targetRect.Min, targetRect.Max), allowCamera: node.Kind != PipelineNodeKind.Mix);
                if (node.Kind == PipelineNodeKind.Mix)
                {
                    DrawConnectionIntoInputPort(drawList, node.InputBId, nodeRects, GetNodeInputPortB(targetRect.Min, targetRect.Max), allowCamera: false);
                }
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
            drawList.AddText(new System.Numerics.Vector2(cameraStart.X - 46f, cameraStart.Y - 14f), ImGui.GetColorU32(new System.Numerics.Vector4(0.7f, 0.9f, 1f, 1f)), "CAM");
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
        var bestIsSecondInput = false;
        var bestDistance = 12f;

        foreach (var node in _nodes)
        {
            if (!nodeRects.TryGetValue(node.Id, out var targetRect))
            {
                continue;
            }

            if (node.Kind == PipelineNodeKind.Stage && node.Stage is not null && node.Stage.IsSourceStage)
            {
                continue;
            }

            var inputA = GetNodeInputPortA(node, targetRect.Min, targetRect.Max);
            if (TryGetConnectionDistanceToPoint(node.InputAId, nodeRects, inputA, allowCamera: node.Kind != PipelineNodeKind.Mix, mousePos, out var distanceA) && distanceA < bestDistance)
            {
                bestDistance = distanceA;
                bestNode = node;
                bestIsSecondInput = false;
            }

            if (node.Kind == PipelineNodeKind.Mix)
            {
                var inputB = GetNodeInputPortB(targetRect.Min, targetRect.Max);
                if (TryGetConnectionDistanceToPoint(node.InputBId, nodeRects, inputB, allowCamera: false, mousePos, out var distanceB) && distanceB < bestDistance)
                {
                    bestDistance = distanceB;
                    bestNode = node;
                    bestIsSecondInput = true;
                }
            }
        }

        if (bestNode is null)
        {
            return false;
        }

        if (bestIsSecondInput)
        {
            bestNode.InputBId = null;
        }
        else
        {
            bestNode.InputAId = null;
        }

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

        var details = node.Kind switch
        {
            PipelineNodeKind.Stage when node.Stage is not null && node.Stage.IsSourceStage => "Source\nOut: 1",
            PipelineNodeKind.Stage => "Effect\nIn: 1 Out: 1",
            PipelineNodeKind.Mix => "Mix\nIn: 2 Out: 1",
            _ => "Output\nIn: 1"
        };
        drawList.AddText(new System.Numerics.Vector2(min.X + 10f, min.Y + 34f), ImGui.GetColorU32(new System.Numerics.Vector4(0.78f, 0.82f, 0.9f, 1f)), details);

        ImGui.SetCursorScreenPos(min);
        var size = new System.Numerics.Vector2(max.X - min.X, max.Y - min.Y);
        ImGui.InvisibleButton($"node_drag_{node.Id}", size);
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var delta = ImGui.GetIO().MouseDelta;
            node.Position.X += delta.X;
            node.Position.Y += delta.Y;
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
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

        if (!(node.Kind == PipelineNodeKind.Stage && node.Stage is not null && node.Stage.IsSourceStage))
        {
            var inputA = GetNodeInputPortA(node, min, max);
            var allowCameraInput = node.Kind != PipelineNodeKind.Mix;
            DrawInputPort(nodeIndex, drawList, inputA, node, useSecondInput: false, allowCamera: allowCameraInput);
        }

        if (node.Kind == PipelineNodeKind.Mix)
        {
            var inputB = GetNodeInputPortB(min, max);
            DrawInputPort(nodeIndex, drawList, inputB, node, useSecondInput: true, allowCamera: false);
        }

        if (node.Kind != PipelineNodeKind.Output)
        {
            var output = GetNodeOutputPort(min, max);
            DrawOutputPort(drawList, output, node);
        }
    }

    private void DrawInputPort(int nodeIndex, ImDrawListPtr drawList, System.Numerics.Vector2 portPos, PipelineNode node, bool useSecondInput, bool allowCamera)
    {
        drawList.AddCircleFilled(portPos, 6f, ImGui.GetColorU32(new System.Numerics.Vector4(0.25f, 0.85f, 1f, 1f)));
        ImGui.SetCursorScreenPos(new System.Numerics.Vector2(portPos.X - 8f, portPos.Y - 8f));
        ImGui.InvisibleButton($"port_in_{node.Id}_{(useSecondInput ? 1 : 0)}", new System.Numerics.Vector2(16f, 16f));
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

            if (useSecondInput)
            {
                node.InputBId = assignment;
            }
            else
            {
                node.InputAId = assignment;
            }

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
            if (targetNode.Kind == PipelineNodeKind.Mix)
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
        if (targetNode.Kind == PipelineNodeKind.Mix)
        {
            if (!targetNode.InputAId.HasValue)
            {
                targetNode.InputAId = assignment;
                assigned = true;
            }
            else if (!targetNode.InputBId.HasValue)
            {
                targetNode.InputBId = assignment;
                assigned = true;
            }
        }
        else if (!targetNode.InputAId.HasValue)
        {
            targetNode.InputAId = assignment;
            assigned = true;
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
                if (candidate.InputAId == current || candidate.InputBId == current)
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
            if (!selected.Stage.IsSourceStage)
            {
                DrawInputNodeSelector("Input", selectedIndex, ref selected.InputAId, allowCamera: true);
            }
            else
            {
                ImGui.TextDisabled("Source node: no input");
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

    private static System.Numerics.Vector2 GetNodeVisualSize(PipelineNode node)
    {
        return node.Kind switch
        {
            PipelineNodeKind.Output => new System.Numerics.Vector2(220f, 96f),
            PipelineNodeKind.Mix => new System.Numerics.Vector2(240f, 120f),
            _ => new System.Numerics.Vector2(240f, 112f)
        };
    }

    private static System.Numerics.Vector2 GetNodeOutputPort(System.Numerics.Vector2 min, System.Numerics.Vector2 max)
    {
        return new System.Numerics.Vector2(max.X, (min.Y + max.Y) * 0.5f);
    }

    private static System.Numerics.Vector2 GetNodeInputPortA(PipelineNode node, System.Numerics.Vector2 min, System.Numerics.Vector2 max)
    {
        if (node.Kind == PipelineNodeKind.Mix)
        {
            return new System.Numerics.Vector2(min.X, min.Y + 42f);
        }

        return new System.Numerics.Vector2(min.X, (min.Y + max.Y) * 0.5f);
    }

    private static System.Numerics.Vector2 GetNodeInputPortB(System.Numerics.Vector2 min, System.Numerics.Vector2 max)
    {
        return new System.Numerics.Vector2(min.X, max.Y - 42f);
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

    public void Render(float[] spectrum, float time)
    {
        GL.Disable(EnableCap.ScissorTest);
        ProcessPendingDisposals();

        HandlePendingCameraReinitialize();
        EnsureCameraInitialized();
        _cameraInput?.UpdateTextureFromLatestFrame();

        EnsureRenderTargets();

        var outputNode = _nodes.FirstOrDefault(x => x.Kind == PipelineNodeKind.Output);
        if (outputNode is null)
        {
            BuildDefaultPipeline();
            outputNode = _nodes.FirstOrDefault(x => x.Kind == PipelineNodeKind.Output);
            if (outputNode is null)
            {
                return;
            }
        }

        var cameraTexture = _cameraInput?.TextureId ?? 0;
        var renderedOutputs = new Dictionary<int, int>();

        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (node.Kind == PipelineNodeKind.Output)
            {
                continue;
            }

            var targetTexture = EnsureNodeOutputTexture(node.Id);
            if (targetTexture == 0)
            {
                continue;
            }

            switch (node.Kind)
            {
                case PipelineNodeKind.Stage when node.Stage is not null:
                {
                    var inputTexture = ResolveNodeInputTexture(node.InputAId, cameraTexture, renderedOutputs, allowCameraFallback: true);
                    if (node.Stage.IsSourceStage)
                    {
                        inputTexture = cameraTexture;
                    }

                    RenderStageNode(node.Stage, inputTexture, targetTexture, spectrum, time);
                    renderedOutputs[node.Id] = targetTexture;
                    break;
                }
                case PipelineNodeKind.Mix when node.MixBox is not null:
                {
                    var inputA = ResolveNodeInputTexture(node.InputAId, cameraTexture, renderedOutputs, allowCameraFallback: false);
                    var inputB = ResolveNodeInputTexture(node.InputBId, cameraTexture, renderedOutputs, allowCameraFallback: false);
                    RenderMixNode(node.MixBox, inputA, inputB, targetTexture);
                    renderedOutputs[node.Id] = targetTexture;
                    break;
                }
            }
        }

        var finalTexture = ResolveNodeInputTexture(outputNode.InputAId, cameraTexture, renderedOutputs, allowCameraFallback: true);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (finalTexture != 0)
        {
            DrawFullscreen(_blitProgram, finalTexture);
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    private void AddStageNode(PipelineStage stage)
    {
        var node = new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Stage,
            Stage = stage,
            Position = GetAutoNodePosition(_nodes.Count)
        };

        if (!stage.IsSourceStage)
        {
            node.InputAId = GetLastRenderableNodeId();
        }

        InsertBeforeOutputNode(node);
        _selectedNodeId = node.Id;
        RebuildParameters();
    }

    private void AddMixNode()
    {
        var previous = GetLastTwoRenderableNodeIds();
        var node = new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Mix,
            MixBox = new MixBoxNode(),
            InputAId = previous.First,
            InputBId = previous.Second,
            Position = GetAutoNodePosition(_nodes.Count)
        };

        InsertBeforeOutputNode(node);
        _selectedNodeId = node.Id;
        RebuildParameters();
    }

    private void InsertBeforeOutputNode(PipelineNode node)
    {
        var outputIndex = _nodes.FindIndex(x => x.Kind == PipelineNodeKind.Output);
        if (outputIndex < 0)
        {
            _nodes.Add(node);
            EnsureOutputNode();
            return;
        }

        _nodes.Insert(outputIndex, node);
    }

    private void EnsureOutputNode()
    {
        if (_nodes.Any(x => x.Kind == PipelineNodeKind.Output))
        {
            return;
        }

        _nodes.Add(new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Output,
            InputAId = GetLastRenderableNodeId(),
            Position = GetAutoNodePosition(Math.Max(0, _nodes.Count))
        });
    }

    private int? GetLastRenderableNodeId()
    {
        for (var i = _nodes.Count - 1; i >= 0; i--)
        {
            if (_nodes[i].Kind != PipelineNodeKind.Output)
            {
                return _nodes[i].Id;
            }
        }

        return null;
    }

    private (int? First, int? Second) GetLastTwoRenderableNodeIds()
    {
        int? first = null;
        int? second = null;

        for (var i = _nodes.Count - 1; i >= 0; i--)
        {
            if (_nodes[i].Kind == PipelineNodeKind.Output)
            {
                continue;
            }

            if (first is null)
            {
                first = _nodes[i].Id;
            }
            else
            {
                second = _nodes[i].Id;
                break;
            }
        }

        return (first, second);
    }

    private void DrawInputNodeSelector(string label, int currentNodeIndex, ref int? selectedNodeId, bool allowCamera)
    {
        var preview = selectedNodeId.HasValue
            ? GetNodeLabel(selectedNodeId.Value)
            : allowCamera
                ? "Camera"
                : "None";

        if (!ImGui.BeginCombo(label, preview))
        {
            return;
        }

        if (allowCamera)
        {
            var isCamera = !selectedNodeId.HasValue;
            if (ImGui.Selectable("Camera", isCamera))
            {
                selectedNodeId = null;
            }

            if (isCamera)
            {
                ImGui.SetItemDefaultFocus();
            }
        }
        else
        {
            var isNone = !selectedNodeId.HasValue;
            if (ImGui.Selectable("None", isNone))
            {
                selectedNodeId = null;
            }

            if (isNone)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        for (var i = 0; i < currentNodeIndex; i++)
        {
            var candidate = _nodes[i];
            if (candidate.Kind == PipelineNodeKind.Output)
            {
                continue;
            }

            var selected = selectedNodeId == candidate.Id;
            var candidateLabel = GetNodeLabel(candidate.Id);
            if (ImGui.Selectable(candidateLabel, selected))
            {
                selectedNodeId = candidate.Id;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    private string GetNodeLabel(int nodeId)
    {
        if (nodeId == CameraVirtualNodeId)
        {
            return "Camera";
        }

        var node = _nodes.FirstOrDefault(x => x.Id == nodeId);
        return node is null ? $"Node {nodeId}" : GetNodeLabel(node);
    }

    private static string GetNodeLabel(PipelineNode node)
    {
        return node.Kind switch
        {
            PipelineNodeKind.Stage when node.Stage is not null => $"[{node.Id}] {node.Stage.Name}",
            PipelineNodeKind.Mix => $"[{node.Id}] Mix Box",
            PipelineNodeKind.Output => $"[{node.Id}] Out Box",
            _ => $"[{node.Id}] Node"
        };
    }

    private int ResolveNodeInputTexture(int? inputNodeId, int cameraTexture, IReadOnlyDictionary<int, int> renderedOutputs, bool allowCameraFallback)
    {
        if (inputNodeId.HasValue && renderedOutputs.TryGetValue(inputNodeId.Value, out var texture))
        {
            return texture;
        }

        if (allowCameraFallback)
        {
            return cameraTexture;
        }

        return 0;
    }

    private void RenderStageNode(PipelineStage stage, int inputTexture, int outputTexture, float[] spectrum, float time)
    {
        AttachTextureToStageFbo(_stageTexture);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        stage.EnsureResources(this);
        stage.Render(this, inputTexture, spectrum, time);

        AttachTextureToStageFbo(outputTexture);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (stage.IsSourceStage)
        {
            DrawFullscreen(_blitProgram, _stageTexture);
        }
        else
        {
            DrawBlendTextures(inputTexture, _stageTexture, stage.StageMix.CurrentValue, stage.BlendMode.CurrentValue);
        }
    }

    private void RenderMixNode(MixBoxNode mixBox, int inputA, int inputB, int outputTexture)
    {
        AttachTextureToStageFbo(outputTexture);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        DrawBlendTextures(inputA, inputB, mixBox.Mix.CurrentValue, mixBox.BlendMode.CurrentValue);
    }

    private void DrawBlendTextures(int baseTexture, int layerTexture, float mix, int blendMode)
    {
        GL.UseProgram(_blendProgram);
        GL.Uniform1(_uBlendMix, mix);
        GL.Uniform1(_uBlendMode, blendMode);
        DrawFullscreenWithTextures(_blendProgram, (0, baseTexture), (1, layerTexture));
    }

    private void AttachTextureToStageFbo(int textureId)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _stageFbo);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            textureId,
            0);
    }

    private int EnsureNodeOutputTexture(int nodeId)
    {
        if (_renderWidth <= 0 || _renderHeight <= 0)
        {
            return 0;
        }

        if (_nodeOutputTextures.TryGetValue(nodeId, out var texture) && texture != 0)
        {
            return texture;
        }

        texture = CreateRenderTexture(_renderWidth, _renderHeight);
        _nodeOutputTextures[nodeId] = texture;
        return texture;
    }

    private void RemoveNodeAt(int index)
    {
        var node = _nodes[index];
        if (node.Stage is not null)
        {
            _pendingDisposals.Enqueue(node.Stage);
        }

        _nodes.RemoveAt(index);
        if (_nodeOutputTextures.TryGetValue(node.Id, out var texture) && texture != 0)
        {
            GL.DeleteTexture(texture);
            _nodeOutputTextures.Remove(node.Id);
        }

        if (_selectedNodeId == node.Id)
        {
            _selectedNodeId = null;
        }

        if (_linkStartNodeId == node.Id)
        {
            _linkStartNodeId = null;
        }

        SanitizeNodeConnections();
        RebuildParameters();
    }

    private void SanitizeNodeConnections()
    {
        var liveIds = new HashSet<int>(_nodes.Select(x => x.Id));
        foreach (var node in _nodes)
        {
            if (node.Kind == PipelineNodeKind.Stage && node.Stage is not null && node.Stage.IsSourceStage)
            {
                node.InputAId = null;
            }
            else if (node.InputAId.HasValue && !liveIds.Contains(node.InputAId.Value))
            {
                node.InputAId = null;
            }

            if (node.InputBId.HasValue && !liveIds.Contains(node.InputBId.Value))
            {
                node.InputBId = null;
            }
        }

        EnsureOutputNode();
        ReorderNodesTopologically();

        var outputNodes = _nodes.Where(x => x.Kind == PipelineNodeKind.Output).ToList();
        if (outputNodes.Count > 1)
        {
            for (var i = 1; i < outputNodes.Count; i++)
            {
                var duplicate = outputNodes[i];
                _nodes.Remove(duplicate);
            }
        }

        var outputNode = _nodes.FirstOrDefault(x => x.Kind == PipelineNodeKind.Output);
        if (outputNode is not null)
        {
            _nodes.Remove(outputNode);
            _nodes.Add(outputNode);
        }
    }

    private void ReorderNodesTopologically()
    {
        if (_nodes.Count <= 1)
        {
            return;
        }

        var ids = _nodes.Select(x => x.Id).ToHashSet();
        var originalIndex = new Dictionary<int, int>(_nodes.Count);
        for (var i = 0; i < _nodes.Count; i++)
        {
            originalIndex[_nodes[i].Id] = i;
        }

        var inDegree = _nodes.ToDictionary(x => x.Id, _ => 0);
        var adjacency = _nodes.ToDictionary(x => x.Id, _ => new List<int>());

        foreach (var node in _nodes)
        {
            if (node.InputAId.HasValue && ids.Contains(node.InputAId.Value) && node.InputAId.Value != node.Id)
            {
                adjacency[node.InputAId.Value].Add(node.Id);
                inDegree[node.Id]++;
            }

            if (node.InputBId.HasValue && ids.Contains(node.InputBId.Value) && node.InputBId.Value != node.Id)
            {
                adjacency[node.InputBId.Value].Add(node.Id);
                inDegree[node.Id]++;
            }
        }

        var available = _nodes
            .Where(x => inDegree[x.Id] == 0)
            .OrderBy(x => originalIndex[x.Id])
            .Select(x => x.Id)
            .ToList();

        var orderedIds = new List<int>(_nodes.Count);
        while (available.Count > 0)
        {
            var nextId = available[0];
            available.RemoveAt(0);
            orderedIds.Add(nextId);

            foreach (var dependentId in adjacency[nextId])
            {
                inDegree[dependentId]--;
                if (inDegree[dependentId] == 0)
                {
                    available.Add(dependentId);
                }
            }

            available.Sort((a, b) => originalIndex[a].CompareTo(originalIndex[b]));
        }

        if (orderedIds.Count != _nodes.Count)
        {
            return;
        }

        var byId = _nodes.ToDictionary(x => x.Id);
        _nodes.Clear();
        foreach (var id in orderedIds)
        {
            _nodes.Add(byId[id]);
        }
    }

    private void ApplyGraphPresetState(VisualPipelinePresetState state)
    {
        ClearStages(deferDispose: true);

        var idMap = new Dictionary<int, int>();
        var nodeStateByNewId = new Dictionary<int, VisualPipelineNodePresetState>();

        foreach (var nodeState in state.Nodes)
        {
            if (!Enum.TryParse<PipelineNodeKind>(nodeState.NodeKind, out var kind))
            {
                continue;
            }

            PipelineNode? node = kind switch
            {
                PipelineNodeKind.Stage when !string.IsNullOrWhiteSpace(nodeState.StageTypeId) => new PipelineNode
                {
                    Id = _nextNodeId++,
                    Kind = PipelineNodeKind.Stage,
                    Stage = CreateStageByTypeId(nodeState.StageTypeId!)
                },
                PipelineNodeKind.Mix => new PipelineNode
                {
                    Id = _nextNodeId++,
                    Kind = PipelineNodeKind.Mix,
                    MixBox = new MixBoxNode()
                },
                PipelineNodeKind.Output => new PipelineNode
                {
                    Id = _nextNodeId++,
                    Kind = PipelineNodeKind.Output
                },
                _ => null
            };

            if (node is null || (kind == PipelineNodeKind.Stage && node.Stage is null))
            {
                continue;
            }

            node.Position = new System.Numerics.Vector2(nodeState.PositionX, nodeState.PositionY);

            foreach (var parameter in node.GetAllParameters())
            {
                if (nodeState.ParameterValues.TryGetValue(parameter.Name, out var numericValue))
                {
                    SetParameterNumericValue(parameter, numericValue);
                }
            }

            _nodes.Add(node);
            idMap[nodeState.NodeId] = node.Id;
            nodeStateByNewId[node.Id] = nodeState;
        }

        foreach (var node in _nodes)
        {
            if (!nodeStateByNewId.TryGetValue(node.Id, out var nodeState))
            {
                continue;
            }

            node.InputAId = nodeState.InputAId.HasValue && idMap.TryGetValue(nodeState.InputAId.Value, out var mappedA) ? mappedA : null;
            node.InputBId = nodeState.InputBId.HasValue && idMap.TryGetValue(nodeState.InputBId.Value, out var mappedB) ? mappedB : null;
        }

        SanitizeNodeConnections();
        if (_nodes.Count == 0)
        {
            BuildDefaultPipeline();
            return;
        }

        _selectedNodeId = _nodes[0].Id;
        _linkStartNodeId = null;
        RebuildParameters();
    }

    private void ApplyLegacySequentialPresetState(IReadOnlyList<VisualPipelineStagePresetState> stages)
    {
        ClearStages(deferDispose: true);

        PipelineNode? previous = null;
        foreach (var stageState in stages)
        {
            var stage = CreateStageByTypeId(stageState.StageTypeId);
            if (stage is null)
            {
                continue;
            }

            var node = new PipelineNode
            {
                Id = _nextNodeId++,
                Kind = PipelineNodeKind.Stage,
                Stage = stage,
                InputAId = stage.IsSourceStage ? null : previous?.Id,
                Position = GetAutoNodePosition(_nodes.Count)
            };

            foreach (var parameter in stage.GetAllParameters())
            {
                if (stageState.ParameterValues.TryGetValue(parameter.Name, out var numericValue))
                {
                    SetParameterNumericValue(parameter, numericValue);
                }
            }

            if (stage.RefreshDynamicParameters())
            {
                foreach (var parameter in stage.GetAllParameters())
                {
                    if (stageState.ParameterValues.TryGetValue(parameter.Name, out var numericValue))
                    {
                        SetParameterNumericValue(parameter, numericValue);
                    }
                }
            }

            _nodes.Add(node);
            previous = node;
        }

        EnsureOutputNode();
        var output = _nodes.FirstOrDefault(x => x.Kind == PipelineNodeKind.Output);
        if (output is not null)
        {
            output.InputAId = previous?.Id;
        }

        if (_nodes.Count == 0)
        {
            BuildDefaultPipeline();
            return;
        }

        if (output is not null)
        {
            output.Position = GetAutoNodePosition(Math.Max(0, _nodes.Count - 1));
        }

        _selectedNodeId = _nodes[0].Id;
        _linkStartNodeId = null;
        RebuildParameters();
    }

    private void HandlePendingCameraReinitialize()
    {
        if (!_cameraReinitPending)
        {
            return;
        }

        _cameraInput?.Dispose();
        _cameraInput = null;
        _cameraReinitPending = false;
        _cameraStatus = $"Reinitializing device {_selectedDeviceIndex}...";
    }

    private void EnsureCameraInitialized()
    {
        if (_cameraInput is not null)
        {
            return;
        }

        if (_deviceIndices.Count == 0)
        {
            RefreshDevices();
            if (_deviceIndices.Count == 0)
            {
                return;
            }
        }

        try
        {
            _cameraInput = new CameraInput(_selectedDeviceIndex);
            _cameraStatus = $"Running (device {_selectedDeviceIndex})";
        }
        catch (Exception ex)
        {
            _cameraInput = null;
            _cameraStatus = $"Failed to open device {_selectedDeviceIndex}: {ex.Message}";
        }
    }

    private void EnsureRenderTargets()
    {
        var viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);

        var width = Math.Max(1, viewport[2]);
        var height = Math.Max(1, viewport[3]);

        if (width == _renderWidth && height == _renderHeight && _stageTexture != 0)
        {
            foreach (var node in _nodes)
            {
                node.Stage?.OnResize(width, height, this);
            }

            return;
        }

        _renderWidth = width;
        _renderHeight = height;

        if (_stageTexture != 0)
        {
            GL.DeleteTexture(_stageTexture);
            _stageTexture = 0;
        }

        if (_stageFbo != 0)
        {
            GL.DeleteFramebuffer(_stageFbo);
            _stageFbo = 0;
        }

        foreach (var texture in _nodeOutputTextures.Values)
        {
            if (texture != 0)
            {
                GL.DeleteTexture(texture);
            }
        }

        _nodeOutputTextures.Clear();

        _stageTexture = CreateRenderTexture(width, height);
        _stageFbo = GL.GenFramebuffer();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _stageFbo);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _stageTexture,
            0);

        var stageStatus = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (stageStatus != FramebufferErrorCode.FramebufferComplete)
        {
            throw new InvalidOperationException($"VisualPipeline stage framebuffer incomplete: {stageStatus}");
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        foreach (var node in _nodes)
        {
            node.Stage?.OnResize(width, height, this);
        }
    }

    private static int CreateRenderTexture(int width, int height)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba,
            width,
            height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            IntPtr.Zero);

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private void CreateGlResources()
    {
        const string vertexSource = """
            #version 330 core
            layout (location = 0) in vec2 aPosition;
            layout (location = 1) in vec2 aUv;

            out vec2 vUv;

            void main()
            {
                vUv = aUv;
                gl_Position = vec4(aPosition, 0.0, 1.0);
            }
            """;

        const string blitFragment = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColor;
            uniform sampler2D uTexture;
            void main()
            {
                fragColor = texture(uTexture, vUv);
            }
            """;

        const string blitFlipFragment = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColor;
            uniform sampler2D uTexture;
            void main()
            {
                fragColor = texture(uTexture, vec2(vUv.x, 1.0 - vUv.y));
            }
            """;

        _blitProgram = CompileProgram(vertexSource, blitFragment);
        _blitProgramFlipY = CompileProgram(vertexSource, blitFlipFragment);

        GL.UseProgram(_blitProgram);
        GL.Uniform1(GL.GetUniformLocation(_blitProgram, "uTexture"), 0);
        GL.UseProgram(_blitProgramFlipY);
        GL.Uniform1(GL.GetUniformLocation(_blitProgramFlipY, "uTexture"), 0);
        GL.UseProgram(0);

        var vertices = new float[]
        {
            -1f, -1f,   0f, 0f,
             1f, -1f,   1f, 0f,
             1f,  1f,   1f, 1f,
            -1f,  1f,   0f, 1f
        };

        var indices = new uint[] { 0, 1, 2, 2, 3, 0 };

        _quadVao = GL.GenVertexArray();
        _quadVbo = GL.GenBuffer();
        _quadEbo = GL.GenBuffer();

        GL.BindVertexArray(_quadVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _quadVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _quadEbo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);

        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));

        GL.BindVertexArray(0);

        _copyFboRead = GL.GenFramebuffer();
        _copyFboDraw = GL.GenFramebuffer();

        const string blendFragment = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColor;

            uniform sampler2D uBaseTexture;
            uniform sampler2D uLayerTexture;
            uniform float uMix;
            uniform int uBlendMode;

            vec3 blendMode(vec3 baseColor, vec3 layerColor, int mode)
            {
                if (mode == 1) return max(baseColor - layerColor, vec3(0.0));
                if (mode == 2) return baseColor * layerColor;
                if (mode == 3) return min(baseColor, layerColor);
                if (mode == 4) return max(baseColor, layerColor);
                if (mode == 5) return 1.0 - ((1.0 - baseColor) * (1.0 - layerColor));
                if (mode == 6) return abs(baseColor - layerColor);
                if (mode == 7)
                {
                    vec3 low = 2.0 * baseColor * layerColor;
                    vec3 high = 1.0 - (2.0 * (1.0 - baseColor) * (1.0 - layerColor));
                    return mix(low, high, step(vec3(0.5), baseColor));
                }
                if (mode == 8)
                {
                    vec3 low = 2.0 * baseColor * layerColor;
                    vec3 high = 1.0 - (2.0 * (1.0 - baseColor) * (1.0 - layerColor));
                    return mix(low, high, step(vec3(0.5), layerColor));
                }
                if (mode == 9) return clamp(baseColor / max(layerColor, vec3(0.001)), 0.0, 1.0);
                if (mode == 10) return clamp(baseColor / max(vec3(0.001), 1.0 - layerColor), 0.0, 1.0);
                return layerColor;
            }

            void main()
            {
                vec3 baseColor = texture(uBaseTexture, vUv).rgb;
                vec3 layerColor = texture(uLayerTexture, vUv).rgb;
                vec3 blended = blendMode(baseColor, layerColor, clamp(uBlendMode, 0, 10));
                vec3 color = mix(baseColor, blended, clamp(uMix, 0.0, 1.0));
                fragColor = vec4(color, 1.0);
            }
            """;

        _blendProgram = CompileProgram(vertexSource, blendFragment);
        _uBlendBaseTexture = GL.GetUniformLocation(_blendProgram, "uBaseTexture");
        _uBlendLayerTexture = GL.GetUniformLocation(_blendProgram, "uLayerTexture");
        _uBlendMix = GL.GetUniformLocation(_blendProgram, "uMix");
        _uBlendMode = GL.GetUniformLocation(_blendProgram, "uBlendMode");

        GL.UseProgram(_blendProgram);
        GL.Uniform1(_uBlendBaseTexture, 0);
        GL.Uniform1(_uBlendLayerTexture, 1);
        GL.UseProgram(0);
    }

    internal void DrawFullscreen(int shaderProgram, int inputTexture)
    {
        GL.UseProgram(shaderProgram);
        GL.BindVertexArray(_quadVao);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, inputTexture);

        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
    }

    internal void DrawFullscreenWithTextures(int shaderProgram, params (int TextureUnitIndex, int TextureId)[] textures)
    {
        GL.UseProgram(shaderProgram);
        GL.BindVertexArray(_quadVao);

        foreach (var (unit, texture) in textures)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(TextureTarget.Texture2D, texture);
        }

        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
    }

    internal void DrawFullscreenWithTextureBindings(int shaderProgram, params (int TextureUnitIndex, TextureTarget Target, int TextureId)[] textures)
    {
        GL.UseProgram(shaderProgram);
        GL.BindVertexArray(_quadVao);

        foreach (var (unit, target, texture) in textures)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(target, texture);
        }

        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
    }

    internal void CopyTexture(int sourceTexture, int targetTexture)
    {
        if (sourceTexture == 0 || targetTexture == 0 || _renderWidth <= 0 || _renderHeight <= 0)
        {
            return;
        }

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _copyFboRead);
        GL.FramebufferTexture2D(
            FramebufferTarget.ReadFramebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            sourceTexture,
            0);

        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _copyFboDraw);
        GL.FramebufferTexture2D(
            FramebufferTarget.DrawFramebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            targetTexture,
            0);

        GL.BlitFramebuffer(
            0,
            0,
            _renderWidth,
            _renderHeight,
            0,
            0,
            _renderWidth,
            _renderHeight,
            ClearBufferMask.ColorBufferBit,
            BlitFramebufferFilter.Nearest);

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
    }

    private void RefreshDynamicStageParameters()
    {
        var changed = false;
        foreach (var node in _nodes)
        {
            if (node.Stage is not null && node.Stage.RefreshDynamicParameters())
            {
                changed = true;
            }
        }

        if (changed)
        {
            RebuildParameters();
        }
    }

    private void RebuildParameters()
    {
        _parameters.Clear();
        foreach (var node in _nodes)
        {
            _parameters.AddRange(node.GetAllParameters());
        }
    }

    private void BuildDefaultPipeline()
    {
        ClearStages(deferDispose: true);

        var camera = new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Stage,
            Stage = new CameraSourceStage(),
            Position = new System.Numerics.Vector2(40f, 80f)
        };

        var edge = new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Stage,
            Stage = new EdgeDetectEffectStage(),
            InputAId = camera.Id,
            Position = new System.Numerics.Vector2(360f, 80f)
        };

        var snapshot = new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Stage,
            Stage = new SnapshotPeakEffectStage(),
            InputAId = edge.Id,
            Position = new System.Numerics.Vector2(680f, 80f)
        };

        var output = new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Output,
            InputAId = snapshot.Id,
            Position = new System.Numerics.Vector2(1000f, 94f)
        };

        _nodes.Add(camera);
        _nodes.Add(edge);
        _nodes.Add(snapshot);
        _nodes.Add(output);

        _selectedNodeId = camera.Id;
        _linkStartNodeId = null;
        RebuildParameters();
    }

    private static float GetParameterNumericValue(IParameter parameter)
    {
        return parameter switch
        {
            Parameter<int> intParameter => intParameter.Value,
            Parameter<float> floatParameter => floatParameter.Value,
            _ => 0f
        };
    }

    private static void SetParameterNumericValue(IParameter parameter, float numericValue)
    {
        switch (parameter)
        {
            case Parameter<int> intParameter:
                intParameter.Value = Math.Clamp((int)MathF.Round(numericValue), intParameter.Min, intParameter.Max);
                break;
            case Parameter<float> floatParameter:
                floatParameter.Value = Math.Clamp(numericValue, floatParameter.Min, floatParameter.Max);
                break;
        }
    }

    private static PipelineStage? CreateStageByTypeId(string typeId)
    {
        var factory = StageFactories.FirstOrDefault(x => x.TypeId == typeId);
        return factory?.Create();
    }

    private void ProcessPendingDisposals()
    {
        while (_pendingDisposals.Count > 0)
        {
            var stage = _pendingDisposals.Dequeue();
            stage.Dispose();
        }
    }

    private void ClearStages(bool deferDispose)
    {
        foreach (var node in _nodes)
        {
            if (node.Stage is null)
            {
                continue;
            }

            if (deferDispose)
            {
                _pendingDisposals.Enqueue(node.Stage);
            }
            else
            {
                node.Stage.Dispose();
            }
        }

        _nodes.Clear();

        foreach (var texture in _nodeOutputTextures.Values)
        {
            if (texture != 0)
            {
                GL.DeleteTexture(texture);
            }
        }

        _nodeOutputTextures.Clear();
        _selectedNodeId = null;
        _linkStartNodeId = null;
    }

    private static int CompileProgram(string vertexSource, string fragmentSource)
    {
        var vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, vertexSource);
        GL.CompileShader(vs);
        GL.GetShader(vs, ShaderParameter.CompileStatus, out var vsOk);
        if (vsOk == 0)
        {
            throw new InvalidOperationException($"VisualPipeline vertex shader compile failed: {GL.GetShaderInfoLog(vs)}");
        }

        var fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fragmentSource);
        GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out var fsOk);
        if (fsOk == 0)
        {
            throw new InvalidOperationException($"VisualPipeline fragment shader compile failed: {GL.GetShaderInfoLog(fs)}");
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linkOk);
        if (linkOk == 0)
        {
            throw new InvalidOperationException($"VisualPipeline shader link failed: {GL.GetProgramInfoLog(program)}");
        }

        GL.DetachShader(program, vs);
        GL.DetachShader(program, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);

        return program;
    }

    public void Dispose()
    {
        _cameraInput?.Dispose();
        ClearStages(deferDispose: false);
        ProcessPendingDisposals();

        if (_copyFboRead != 0) GL.DeleteFramebuffer(_copyFboRead);
        if (_copyFboDraw != 0) GL.DeleteFramebuffer(_copyFboDraw);
        if (_stageFbo != 0) GL.DeleteFramebuffer(_stageFbo);
        if (_stageTexture != 0) GL.DeleteTexture(_stageTexture);

        if (_quadEbo != 0) GL.DeleteBuffer(_quadEbo);
        if (_quadVbo != 0) GL.DeleteBuffer(_quadVbo);
        if (_quadVao != 0) GL.DeleteVertexArray(_quadVao);

        if (_blitProgram != 0) GL.DeleteProgram(_blitProgram);
        if (_blitProgramFlipY != 0) GL.DeleteProgram(_blitProgramFlipY);
        if (_blendProgram != 0) GL.DeleteProgram(_blendProgram);
    }

    private sealed record StageFactory(string TypeId, string Label, Func<PipelineStage> Create);

    private enum PipelineNodeKind
    {
        Stage,
        Mix,
        Output
    }

    private abstract class PipelineStage : IDisposable
    {
        private readonly Parameter<float> _stageMix = new("Stage / Mix", 0f, 1f, 1f);
        private readonly Parameter<int> _blendMode = new("Stage / Blend Mode (0 Normal,1 Subtract,2 Multiply,3 Darker,4 Brighter,5 Screen,6 Difference,7 Overlay,8 Hard Light,9 Divide,10 Color Dodge)", 0, 10, 0);

        public abstract string TypeId { get; }
        public abstract string Name { get; }
        public abstract IReadOnlyList<IParameter> Parameters { get; }
        public virtual bool IsSourceStage => false;
        public Parameter<float> StageMix => _stageMix;
        public Parameter<int> BlendMode => _blendMode;

        public IEnumerable<IParameter> GetAllParameters()
        {
            yield return _stageMix;
            yield return _blendMode;

            foreach (var parameter in Parameters)
            {
                yield return parameter;
            }
        }

        public virtual bool RefreshDynamicParameters()
        {
            return false;
        }

        public virtual void EnsureResources(VisualPipeline host)
        {
        }

        public virtual void OnResize(int width, int height, VisualPipeline host)
        {
        }

        public abstract void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time);

        public virtual void Dispose()
        {
        }
    }

    private sealed class MixBoxNode
    {
        private readonly Parameter<float> _mix = new("Mix Box / Mix", 0f, 1f, 1f);
        private readonly Parameter<int> _blendMode = new("Mix Box / Blend Mode (0 Normal,1 Subtract,2 Multiply,3 Darker,4 Brighter,5 Screen,6 Difference,7 Overlay,8 Hard Light,9 Divide,10 Color Dodge)", 0, 10, 0);

        public Parameter<float> Mix => _mix;
        public Parameter<int> BlendMode => _blendMode;

        public IEnumerable<IParameter> GetAllParameters()
        {
            yield return _mix;
            yield return _blendMode;
        }
    }

    private sealed class PipelineNode
    {
        public int Id;
        public PipelineNodeKind Kind;
        public PipelineStage? Stage;
        public MixBoxNode? MixBox;
        public int? InputAId;
        public int? InputBId;
        public System.Numerics.Vector2 Position;

        public IEnumerable<IParameter> GetAllParameters()
        {
            if (Stage is not null)
            {
                foreach (var parameter in Stage.GetAllParameters())
                {
                    yield return parameter;
                }
            }

            if (MixBox is not null)
            {
                foreach (var parameter in MixBox.GetAllParameters())
                {
                    yield return parameter;
                }
            }
        }
    }
}

public sealed class VisualPipelinePresetState
{
    public List<VisualPipelineNodePresetState> Nodes { get; set; } = [];
    public List<VisualPipelineStagePresetState> Stages { get; set; } = [];
}

public sealed class VisualPipelineNodePresetState
{
    public int NodeId { get; set; }
    public string NodeKind { get; set; } = "Stage";
    public string? StageTypeId { get; set; }
    public int? InputAId { get; set; }
    public int? InputBId { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public Dictionary<string, float> ParameterValues { get; set; } = [];
}

public sealed class VisualPipelineStagePresetState
{
    public string StageTypeId { get; set; } = string.Empty;
    public string InputSource { get; set; } = "Previous";
    public Dictionary<string, float> ParameterValues { get; set; } = [];
}
