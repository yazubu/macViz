namespace macViz;

public sealed partial class VisualPipeline : IVisual, IVisualEditorPanel
{
    private const int MaxSnapshots = 8;
    private const int CameraVirtualNodeId = 0;

    private readonly List<PipelineNode> _nodes = [];
    private readonly Queue<PipelineStage> _pendingDisposals = new();
    private readonly List<IParameter> _parameters = [];
    private readonly Dictionary<int, int> _nodeOutputTextures = [];

    private int _nextNodeId = 1;

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
    private readonly Dictionary<int, string> _staticImagePathDraftByNode = [];
    private readonly Dictionary<int, string> _recorderOutputPathDraftByNode = [];
    private System.Numerics.Vector2 _canvasPan = new(24f, 24f);
    private float _canvasZoom = 1f;

    private static readonly StageFactory[] StageFactories =
    [
        new(CameraSourceStage.TypeIdValue, "Camera Source", () => new CameraSourceStage()),
        new(StaticImageSourceStage.TypeIdValue, "Static Images Source", () => new StaticImageSourceStage()),
        new(SourceVisualStage.RotatingCubeTypeId, "Rotating Cube Source", () => new SourceVisualStage("Rotating Cube", new RotatingCube3D(), SourceVisualStage.RotatingCubeTypeId)),
        new(SourceVisualStage.SpectrumBarsTypeId, "Spectrum Bars Source", () => new SourceVisualStage("Spectrum Bars", new SpectrumBars2d(), SourceVisualStage.SpectrumBarsTypeId)),
        new(SourceVisualStage.ParticleSystemTypeId, "Particle System Source", () => new SourceVisualStage("Particle System", new RotatingParticleSystem3D(), SourceVisualStage.ParticleSystemTypeId)),
        new(SourceVisualStage.CymaticSpiralsTypeId, "Cymatic Spirals Source", () => new SourceVisualStage("Cymatic Spirals", new CymaticSpirals3D(), SourceVisualStage.CymaticSpiralsTypeId)),
        new(SourceVisualStage.DiffusionPaintingTypeId, "Diffusion Painting Source", () => new SourceVisualStage("Diffusion Painting", new DiffusionPainting2D(), SourceVisualStage.DiffusionPaintingTypeId)),
        new(SignalSwitchStage.TypeIdValue, "Signal Switch", () => new SignalSwitchStage()),
        new(PassThroughRecorderStage.TypeIdValue, "Pass Through Recorder", () => new PassThroughRecorderStage()),
        new(EdgeDetectEffectStage.TypeIdValue, "Edge Detection Effect", () => new EdgeDetectEffectStage()),
        new(EdgeRadianceEffectStage.TypeIdValue, "Edge Radiance Effect", () => new EdgeRadianceEffectStage()),
        new(SnapshotPeakEffectStage.TypeIdValue, "Snapshot Peak Hold Effect", () => new SnapshotPeakEffectStage()),
        new(FrameFreezeEffectStage.TypeIdValue, "Frame Freeze Effect", () => new FrameFreezeEffectStage()),
        new(FlipEffectStage.TypeIdValue, "Flip Effect", () => new FlipEffectStage()),
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
        new(BleedingEdgeEffectStage.TypeIdValue, "Bleeding Edge Effect", () => new BleedingEdgeEffectStage()),
        new(WetPaintDripEffectStage.TypeIdValue, "Wet Paint Drip Effect", () => new WetPaintDripEffectStage()),
        new(ColorSwapEffectStage.TypeIdValue, "Color Swap Effect", () => new ColorSwapEffectStage()),
        new(TypographicMatrixEffectStage.TypeIdValue, "Typographic Matrix Effect", () => new TypographicMatrixEffectStage()),
        new(KaleidoscopeEffectStage.TypeIdValue, "Kaleidoscope Effect", () => new KaleidoscopeEffectStage()),
        new(InfinityMirrorEffectStage.TypeIdValue, "Infinity Mirror Effect", () => new InfinityMirrorEffectStage())
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

    public VisualPipeline()
    {
        CreateGlResources();
        BuildDefaultPipeline();
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
                InputExtraIds = [.. node.InputExtraIds],
                StageTypeId = node.Stage?.TypeId,
                PositionX = node.Position.X,
                PositionY = node.Position.Y
            };

            foreach (var parameter in node.GetAllParameters())
            {
                nodeState.ParameterValues[parameter.Name] = GetParameterNumericValue(parameter);
            }

            if (node.Stage is StaticImageSourceStage staticImageSourceStage)
            {
                nodeState.SourceImagePaths = [.. staticImageSourceStage.ImagePaths];
            }

            if (node.Stage is PassThroughRecorderStage passThroughRecorderStage)
            {
                nodeState.RecorderOutputDirectory = passThroughRecorderStage.OutputDirectory;
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
}
