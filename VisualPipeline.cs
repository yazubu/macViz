using ImGuiNET;
using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed class VisualPipeline : IVisual, ICameraVisual, IVisualEditorPanel
{
    private const int MaxSnapshots = 8;

    private readonly List<PipelineStage> _stages = [];
    private readonly Queue<PipelineStage> _pendingDisposals = new();
    private readonly List<IParameter> _parameters = [];

    private CameraInput? _cameraInput;
    private List<int> _deviceIndices = [];
    private int _selectedDeviceIndex;
    private string _cameraStatus = "Not initialized";

    private int _quadVao;
    private int _quadVbo;
    private int _quadEbo;

    private int _blitProgram;
    private int _blitProgramFlipY;

    private readonly int[] _pingTextures = new int[2];
    private readonly int[] _pingFbos = new int[2];
    private int _copyFboRead;
    private int _copyFboDraw;
    private int _renderWidth;
    private int _renderHeight;

    private int _newStageTypeIndex;

    private static readonly StageFactory[] StageFactories =
    [
        new(CameraSourceStage.TypeIdValue, "Camera Source", () => new CameraSourceStage()),
        new(SourceVisualStage.RotatingCubeTypeId, "Rotating Cube Source", () => new SourceVisualStage("Rotating Cube", new RotatingCube3D(), SourceVisualStage.RotatingCubeTypeId)),
        new(SourceVisualStage.SpectrumBarsTypeId, "Spectrum Bars Source", () => new SourceVisualStage("Spectrum Bars", new SpectrumBars2d(), SourceVisualStage.SpectrumBarsTypeId)),
        new(SourceVisualStage.ParticleSystemTypeId, "Particle System Source", () => new SourceVisualStage("Particle System", new RotatingParticleSystem3D(), SourceVisualStage.ParticleSystemTypeId)),
        new(EdgeDetectEffectStage.TypeIdValue, "Edge Detection Effect", () => new EdgeDetectEffectStage()),
        new(SnapshotPeakEffectStage.TypeIdValue, "Snapshot Peak Hold Effect", () => new SnapshotPeakEffectStage()),
        new(ScaleEffectStage.TypeIdValue, "Scale Effect", () => new ScaleEffectStage()),
        new(ColorShiftEffectStage.TypeIdValue, "Color Shift Effect", () => new ColorShiftEffectStage()),
        new(PixelateEffectStage.TypeIdValue, "Pixelate Effect", () => new PixelateEffectStage()),
        new(PosterizeEffectStage.TypeIdValue, "Posterize Effect", () => new PosterizeEffectStage()),
        new(RadialBlurEffectStage.TypeIdValue, "Radial Blur Effect", () => new RadialBlurEffectStage()),
        new(ZoomBlurEffectStage.TypeIdValue, "Zoom Blur Effect", () => new ZoomBlurEffectStage()),
        new(MotionBlurEffectStage.TypeIdValue, "Motion Blur Effect", () => new MotionBlurEffectStage()),
        new(ColorSwapEffectStage.TypeIdValue, "Color Swap Effect", () => new ColorSwapEffectStage()),
        new(KaleidoscopeEffectStage.TypeIdValue, "Kaleidoscope Effect", () => new KaleidoscopeEffectStage())
    ];

    public string Name => "Visual Pipeline";
    public IReadOnlyList<IParameter> Parameters => _parameters;

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
        }
    }

    public void SetSelectedDeviceIndex(int deviceIndex)
    {
        if (_selectedDeviceIndex == deviceIndex)
        {
            return;
        }

        _selectedDeviceIndex = deviceIndex;
        _cameraInput?.Dispose();
        _cameraInput = null;
        _cameraStatus = $"Reinitializing device {_selectedDeviceIndex}...";
    }

    public VisualPipelinePresetState CapturePresetState()
    {
        var state = new VisualPipelinePresetState();

        foreach (var stage in _stages)
        {
            var stageState = new VisualPipelineStagePresetState
            {
                StageTypeId = stage.TypeId,
                InputSource = stage.InputSource.ToString()
            };

            foreach (var parameter in stage.Parameters)
            {
                stageState.ParameterValues[parameter.Name] = GetParameterNumericValue(parameter);
            }

            state.Stages.Add(stageState);
        }

        return state;
    }

    public void ApplyPresetState(VisualPipelinePresetState? state)
    {
        if (state is null || state.Stages.Count == 0)
        {
            BuildDefaultPipeline();
            return;
        }

        ClearStages(deferDispose: true);

        foreach (var stageState in state.Stages)
        {
            var stage = CreateStageByTypeId(stageState.StageTypeId);
            if (stage is null)
            {
                continue;
            }

            if (Enum.TryParse<PipelineInputSource>(stageState.InputSource, out var inputSource))
            {
                stage.InputSource = inputSource;
            }

            foreach (var parameter in stage.Parameters)
            {
                if (stageState.ParameterValues.TryGetValue(parameter.Name, out var numericValue))
                {
                    SetParameterNumericValue(parameter, numericValue);
                }
            }

            _stages.Add(stage);
        }

        if (_stages.Count == 0)
        {
            BuildDefaultPipeline();
            return;
        }

        RebuildParameters();
    }

    public void DrawEditorPanel()
    {
        ImGui.Separator();
        ImGui.Text("Pipeline");

        if (_stages.Count == 0)
        {
            ImGui.TextDisabled("Pipeline is empty. Add a stage.");
        }

        var currentFactoryLabel = StageFactories[Math.Clamp(_newStageTypeIndex, 0, StageFactories.Length - 1)].Label;
        if (ImGui.BeginCombo("New Stage", currentFactoryLabel))
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

        if (ImGui.Button("Add Stage"))
        {
            _stages.Add(StageFactories[_newStageTypeIndex].Create());
            RebuildParameters();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset Default Pipeline"))
        {
            BuildDefaultPipeline();
        }

        for (var i = 0; i < _stages.Count; i++)
        {
            var stage = _stages[i];
            ImGui.PushID($"pipeline_stage_{i}");

            if (ImGui.TreeNode($"{i + 1}. {stage.Name}"))
            {
                if (stage.SupportsInputSelection)
                {
                    var input = stage.InputSource;
                    if (ImGui.BeginCombo("Input", input.ToString()))
                    {
                        foreach (PipelineInputSource candidate in Enum.GetValues<PipelineInputSource>())
                        {
                            var selected = candidate == input;
                            if (ImGui.Selectable(candidate.ToString(), selected))
                            {
                                stage.InputSource = candidate;
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
                    ImGui.TextDisabled("Input: stage-defined source");
                }

                var moved = false;
                if (i > 0 && ImGui.Button("Move Up"))
                {
                    (_stages[i - 1], _stages[i]) = (_stages[i], _stages[i - 1]);
                    moved = true;
                }

                ImGui.SameLine();
                if (i < _stages.Count - 1 && ImGui.Button("Move Down"))
                {
                    (_stages[i + 1], _stages[i]) = (_stages[i], _stages[i + 1]);
                    moved = true;
                }

                ImGui.SameLine();
                if (ImGui.Button("Remove Stage"))
                {
                    _pendingDisposals.Enqueue(stage);
                    _stages.RemoveAt(i);
                    RebuildParameters();
                    ImGui.TreePop();
                    ImGui.PopID();
                    break;
                }

                if (moved)
                {
                    RebuildParameters();
                }

                ImGui.TreePop();
            }

            ImGui.PopID();
        }
    }

    public void Render(float[] spectrum, float time)
    {
        GL.Disable(EnableCap.ScissorTest);
        ProcessPendingDisposals();

        EnsureCameraInitialized();
        _cameraInput?.UpdateTextureFromLatestFrame();

        EnsureRenderTargets();

        if (_stages.Count == 0)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, _renderWidth, _renderHeight);
            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            return;
        }

        var pingIndex = 0;
        var previousTexture = 0;
        var cameraTexture = _cameraInput?.TextureId ?? 0;

        for (var i = 0; i < _stages.Count; i++)
        {
            var stage = _stages[i];
            var isLast = i == _stages.Count - 1;

            if (isLast)
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            }
            else
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _pingFbos[pingIndex]);
            }

            GL.Viewport(0, 0, _renderWidth, _renderHeight);
            GL.ClearColor(0f, 0f, 0f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            var inputTexture = stage.InputSource == PipelineInputSource.Camera
                ? cameraTexture
                : (previousTexture != 0 ? previousTexture : cameraTexture);

            stage.EnsureResources(this);
            stage.Render(this, inputTexture, spectrum, time);

            if (!isLast)
            {
                previousTexture = _pingTextures[pingIndex];
                pingIndex = 1 - pingIndex;
            }
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
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

        if (width == _renderWidth && height == _renderHeight && _pingTextures[0] != 0 && _pingTextures[1] != 0)
        {
            foreach (var stage in _stages)
            {
                stage.OnResize(width, height, this);
            }

            return;
        }

        _renderWidth = width;
        _renderHeight = height;

        for (var i = 0; i < 2; i++)
        {
            if (_pingTextures[i] != 0)
            {
                GL.DeleteTexture(_pingTextures[i]);
                _pingTextures[i] = 0;
            }

            if (_pingFbos[i] != 0)
            {
                GL.DeleteFramebuffer(_pingFbos[i]);
                _pingFbos[i] = 0;
            }
        }

        for (var i = 0; i < 2; i++)
        {
            _pingTextures[i] = CreateRenderTexture(width, height);
            _pingFbos[i] = GL.GenFramebuffer();

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _pingFbos[i]);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _pingTextures[i],
                0);

            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                throw new InvalidOperationException($"VisualPipeline ping framebuffer incomplete: {status}");
            }
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        foreach (var stage in _stages)
        {
            stage.OnResize(width, height, this);
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

    private void RebuildParameters()
    {
        _parameters.Clear();
        foreach (var stage in _stages)
        {
            _parameters.AddRange(stage.Parameters);
        }
    }

    private void BuildDefaultPipeline()
    {
        ClearStages(deferDispose: true);
        _stages.Add(new CameraSourceStage());
        _stages.Add(new EdgeDetectEffectStage());
        _stages.Add(new SnapshotPeakEffectStage());
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
        foreach (var stage in _stages)
        {
            if (deferDispose)
            {
                _pendingDisposals.Enqueue(stage);
            }
            else
            {
                stage.Dispose();
            }
        }

        _stages.Clear();
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

        for (var i = 0; i < 2; i++)
        {
            if (_pingFbos[i] != 0) GL.DeleteFramebuffer(_pingFbos[i]);
            if (_pingTextures[i] != 0) GL.DeleteTexture(_pingTextures[i]);
        }

        if (_copyFboRead != 0) GL.DeleteFramebuffer(_copyFboRead);
        if (_copyFboDraw != 0) GL.DeleteFramebuffer(_copyFboDraw);

        if (_quadEbo != 0) GL.DeleteBuffer(_quadEbo);
        if (_quadVbo != 0) GL.DeleteBuffer(_quadVbo);
        if (_quadVao != 0) GL.DeleteVertexArray(_quadVao);

        if (_blitProgram != 0) GL.DeleteProgram(_blitProgram);
        if (_blitProgramFlipY != 0) GL.DeleteProgram(_blitProgramFlipY);
    }

    private sealed record StageFactory(string TypeId, string Label, Func<PipelineStage> Create);

    private enum PipelineInputSource
    {
        Previous,
        Camera
    }

    private abstract class PipelineStage : IDisposable
    {
        public abstract string TypeId { get; }
        public abstract string Name { get; }
        public abstract IReadOnlyList<IParameter> Parameters { get; }
        public virtual bool SupportsInputSelection => true;
        public PipelineInputSource InputSource { get; set; } = PipelineInputSource.Previous;

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

    private sealed class CameraSourceStage : PipelineStage
    {
        public const string TypeIdValue = "camera.source";
        private static readonly IReadOnlyList<IParameter> EmptyParameters = [];

        public override string TypeId => TypeIdValue;
        public override string Name => "Camera Source";
        public override IReadOnlyList<IParameter> Parameters => EmptyParameters;
        public override bool SupportsInputSelection => false;

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            var cameraTexture = host._cameraInput?.TextureId ?? 0;
            host.DrawFullscreen(host._blitProgramFlipY, cameraTexture);
        }
    }

    private sealed class SourceVisualStage(string label, IVisual visual, string typeId) : PipelineStage
    {
        public const string RotatingCubeTypeId = "source.rotatingCube";
        public const string SpectrumBarsTypeId = "source.spectrumBars";
        public const string ParticleSystemTypeId = "source.particleSystem";

        public override string TypeId => typeId;
        public override string Name => label;
        public override IReadOnlyList<IParameter> Parameters => visual.Parameters;
        public override bool SupportsInputSelection => false;

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            visual.Render(spectrum, time);
        }

        public override void Dispose()
        {
            visual.Dispose();
        }
    }

    private sealed class EdgeDetectEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.edgeDetect";
        private readonly Parameter<float> _edgeStrength = new("Edge Effect / Strength", 0f, 5f, 1.4f);
        private readonly Parameter<float> _threshold = new("Edge Effect / Threshold", 0f, 1f, 0.25f);
        private readonly Parameter<float> _mix = new("Edge Effect / Mix", 0f, 1f, 1f);
        private readonly Parameter<float> _invert = new("Edge Effect / Invert", 0f, 1f, 0f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uEdgeStrength;
        private int _uThreshold;
        private int _uMix;
        private int _uInvert;

        public EdgeDetectEffectStage()
        {
            _parameters = [_edgeStrength, _threshold, _mix, _invert];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Edge Detection";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
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

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;

                uniform sampler2D uTexture;
                uniform float uEdgeStrength;
                uniform float uThreshold;
                uniform float uMix;
                uniform float uInvert;

                float lumaAt(vec2 uv)
                {
                    vec3 c = texture(uTexture, uv).rgb;
                    return dot(c, vec3(0.299, 0.587, 0.114));
                }

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;
                    vec2 texSize = vec2(textureSize(uTexture, 0));
                    vec2 texel = 1.0 / max(texSize, vec2(1.0));

                    float tl = lumaAt(vUv + vec2(-texel.x,  texel.y));
                    float tc = lumaAt(vUv + vec2( 0.0,      texel.y));
                    float tr = lumaAt(vUv + vec2( texel.x,  texel.y));
                    float ml = lumaAt(vUv + vec2(-texel.x,  0.0));
                    float mr = lumaAt(vUv + vec2( texel.x,  0.0));
                    float bl = lumaAt(vUv + vec2(-texel.x, -texel.y));
                    float bc = lumaAt(vUv + vec2( 0.0,     -texel.y));
                    float br = lumaAt(vUv + vec2( texel.x, -texel.y));

                    float gx = -tl + tr - 2.0 * ml + 2.0 * mr - bl + br;
                    float gy =  tl + 2.0 * tc + tr - bl - 2.0 * bc - br;

                    float edge = length(vec2(gx, gy)) * max(uEdgeStrength, 0.0);
                    float t = clamp(uThreshold, 0.0, 1.0);
                    float edgeMask = smoothstep(t, min(1.0, t + 0.25), edge);
                    edgeMask = mix(edgeMask, 1.0 - edgeMask, clamp(uInvert, 0.0, 1.0));

                    vec3 edgeColor = vec3(edgeMask);
                    vec3 finalColor = mix(original, edgeColor, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(finalColor, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uEdgeStrength = GL.GetUniformLocation(_program, "uEdgeStrength");
            _uThreshold = GL.GetUniformLocation(_program, "uThreshold");
            _uMix = GL.GetUniformLocation(_program, "uMix");
            _uInvert = GL.GetUniformLocation(_program, "uInvert");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uEdgeStrength, _edgeStrength.CurrentValue);
            GL.Uniform1(_uThreshold, _threshold.CurrentValue);
            GL.Uniform1(_uMix, _mix.CurrentValue);
            GL.Uniform1(_uInvert, _invert.CurrentValue);

            host.DrawFullscreen(_program, inputTexture);
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }
        }
    }

    private sealed class ScaleEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.scale";
        private readonly Parameter<float> _scale = new("Scale Effect / Scale", 0.2f, 3f, 1f);
        private readonly Parameter<float> _pivotX = new("Scale Effect / Pivot X", 0f, 1f, 0.5f);
        private readonly Parameter<float> _pivotY = new("Scale Effect / Pivot Y", 0f, 1f, 0.5f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uScale;
        private int _uPivot;

        public ScaleEffectStage()
        {
            _parameters = [_scale, _pivotX, _pivotY];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Scale";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
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

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;

                uniform sampler2D uTexture;
                uniform float uScale;
                uniform vec2 uPivot;

                void main()
                {
                    float s = max(uScale, 0.001);
                    vec2 uv = ((vUv - uPivot) / s) + uPivot;
                    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
                    {
                        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
                        return;
                    }

                    fragColor = texture(uTexture, uv);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uScale = GL.GetUniformLocation(_program, "uScale");
            _uPivot = GL.GetUniformLocation(_program, "uPivot");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uScale, _scale.CurrentValue);
            GL.Uniform2(_uPivot, _pivotX.CurrentValue, _pivotY.CurrentValue);

            host.DrawFullscreen(_program, inputTexture);
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }
        }
    }

    private sealed class ColorShiftEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.colorShift";
        private readonly Parameter<float> _redShiftPixels = new("Color Shift Effect / Red Shift (px)", -256f, 256f, 8f);
        private readonly Parameter<float> _greenShiftPixels = new("Color Shift Effect / Green Shift (px)", -256f, 256f, 0f);
        private readonly Parameter<float> _blueShiftPixels = new("Color Shift Effect / Blue Shift (px)", -256f, 256f, -8f);
        private readonly Parameter<float> _directionRadians = new("Color Shift Effect / Direction (rad)", -6.28319f, 6.28319f, 0f);
        private readonly Parameter<float> _mix = new("Color Shift Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uRedShiftPixels;
        private int _uGreenShiftPixels;
        private int _uBlueShiftPixels;
        private int _uDirectionRadians;
        private int _uMix;

        public ColorShiftEffectStage()
        {
            _parameters = [_redShiftPixels, _greenShiftPixels, _blueShiftPixels, _directionRadians, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Color Shift";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
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

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;

                uniform sampler2D uTexture;
                uniform float uRedShiftPixels;
                uniform float uGreenShiftPixels;
                uniform float uBlueShiftPixels;
                uniform float uDirectionRadians;
                uniform float uMix;

                vec2 offsetForShift(float pixels, vec2 texel, vec2 dir)
                {
                    return dir * pixels * texel;
                }

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;

                    vec2 texSize = vec2(textureSize(uTexture, 0));
                    vec2 texel = 1.0 / max(texSize, vec2(1.0));
                    vec2 dir = vec2(cos(uDirectionRadians), sin(uDirectionRadians));

                    vec2 uvR = vUv + offsetForShift(uRedShiftPixels, texel, dir);
                    vec2 uvG = vUv + offsetForShift(uGreenShiftPixels, texel, dir);
                    vec2 uvB = vUv + offsetForShift(uBlueShiftPixels, texel, dir);

                    vec3 shifted = vec3(
                        texture(uTexture, uvR).r,
                        texture(uTexture, uvG).g,
                        texture(uTexture, uvB).b
                    );

                    vec3 color = mix(original, shifted, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uRedShiftPixels = GL.GetUniformLocation(_program, "uRedShiftPixels");
            _uGreenShiftPixels = GL.GetUniformLocation(_program, "uGreenShiftPixels");
            _uBlueShiftPixels = GL.GetUniformLocation(_program, "uBlueShiftPixels");
            _uDirectionRadians = GL.GetUniformLocation(_program, "uDirectionRadians");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uRedShiftPixels, _redShiftPixels.CurrentValue);
            GL.Uniform1(_uGreenShiftPixels, _greenShiftPixels.CurrentValue);
            GL.Uniform1(_uBlueShiftPixels, _blueShiftPixels.CurrentValue);
            GL.Uniform1(_uDirectionRadians, _directionRadians.CurrentValue);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            host.DrawFullscreen(_program, inputTexture);
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }
        }
    }

    private sealed class KaleidoscopeEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.kaleidoscope";
        private readonly Parameter<int> _axisCount = new("Kaleidoscope Effect / Axis Count", 1, 24, 6);
        private readonly Parameter<float> _centerX = new("Kaleidoscope Effect / Center X", 0f, 1f, 0.5f);
        private readonly Parameter<float> _centerY = new("Kaleidoscope Effect / Center Y", 0f, 1f, 0.5f);
        private readonly Parameter<float> _axisRotation = new("Kaleidoscope Effect / Axis Rotation (rad)", -6.28319f, 6.28319f, 0f);
        private readonly Parameter<float> _radialScale = new("Kaleidoscope Effect / Radial Scale", 0.2f, 3f, 1f);
        private readonly Parameter<float> _mix = new("Kaleidoscope Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uAxisCount;
        private int _uCenter;
        private int _uAxisRotation;
        private int _uRadialScale;
        private int _uMix;

        public KaleidoscopeEffectStage()
        {
            _parameters = [_axisCount, _centerX, _centerY, _axisRotation, _radialScale, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Kaleidoscope";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
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

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;

                uniform sampler2D uTexture;
                uniform int uAxisCount;
                uniform vec2 uCenter;
                uniform float uAxisRotation;
                uniform float uRadialScale;
                uniform float uMix;

                const float TAU = 6.28318530718;

                vec2 kaleidoscopeUv(vec2 uv)
                {
                    vec2 p = uv - uCenter;
                    float radius = length(p);

                    if (radius <= 0.000001)
                    {
                        return uCenter;
                    }

                    float axisCount = max(float(uAxisCount), 1.0);
                    float segment = TAU / axisCount;

                    float angle = atan(p.y, p.x) - uAxisRotation;
                    angle = mod(angle + 0.5 * segment, segment) - 0.5 * segment;
                    angle = abs(angle);

                    float r = radius / max(uRadialScale, 0.0001);
                    vec2 mirrored = vec2(cos(angle + uAxisRotation), sin(angle + uAxisRotation)) * r;
                    return uCenter + mirrored;
                }

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;
                    vec2 sampledUv = kaleidoscopeUv(vUv);
                    vec3 kaleidoscope = texture(uTexture, sampledUv).rgb;

                    vec3 color = mix(original, kaleidoscope, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uAxisCount = GL.GetUniformLocation(_program, "uAxisCount");
            _uCenter = GL.GetUniformLocation(_program, "uCenter");
            _uAxisRotation = GL.GetUniformLocation(_program, "uAxisRotation");
            _uRadialScale = GL.GetUniformLocation(_program, "uRadialScale");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uAxisCount, _axisCount.CurrentValue);
            GL.Uniform2(_uCenter, _centerX.CurrentValue, _centerY.CurrentValue);
            GL.Uniform1(_uAxisRotation, _axisRotation.CurrentValue);
            GL.Uniform1(_uRadialScale, _radialScale.CurrentValue);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            host.DrawFullscreen(_program, inputTexture);
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }
        }
    }

    private sealed class PixelateEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.pixelate";
        private readonly Parameter<float> _pixelSize = new("Pixelate Effect / Pixel Size", 1f, 128f, 8f);
        private readonly Parameter<float> _mix = new("Pixelate Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uPixelSize;
        private int _uMix;

        public PixelateEffectStage()
        {
            _parameters = [_pixelSize, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Pixelate";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
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

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;

                uniform sampler2D uTexture;
                uniform float uPixelSize;
                uniform float uMix;

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;

                    vec2 texSize = vec2(textureSize(uTexture, 0));
                    vec2 safeSize = max(texSize, vec2(1.0));
                    float block = max(uPixelSize, 1.0);

                    vec2 px = vUv * safeSize;
                    vec2 snapped = (floor(px / block) + 0.5) * block;
                    vec2 uv = clamp(snapped / safeSize, vec2(0.0), vec2(1.0));

                    vec3 pixelated = texture(uTexture, uv).rgb;
                    vec3 color = mix(original, pixelated, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uPixelSize = GL.GetUniformLocation(_program, "uPixelSize");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uPixelSize, _pixelSize.CurrentValue);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            host.DrawFullscreen(_program, inputTexture);
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }
        }
    }

    private sealed class PosterizeEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.posterize";
        private readonly Parameter<int> _levels = new("Posterize Effect / Levels", 2, 32, 6);
        private readonly Parameter<float> _gamma = new("Posterize Effect / Gamma", 0.1f, 3f, 1f);
        private readonly Parameter<float> _mix = new("Posterize Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uLevels;
        private int _uGamma;
        private int _uMix;

        public PosterizeEffectStage()
        {
            _parameters = [_levels, _gamma, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Posterize";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
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

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;

                uniform sampler2D uTexture;
                uniform int uLevels;
                uniform float uGamma;
                uniform float uMix;

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;

                    float levels = max(float(uLevels), 2.0);
                    float gamma = max(uGamma, 0.001);

                    vec3 corrected = pow(max(original, vec3(0.0)), vec3(gamma));
                    vec3 quantized = floor(corrected * (levels - 1.0) + 0.5) / (levels - 1.0);
                    vec3 posterized = pow(max(quantized, vec3(0.0)), vec3(1.0 / gamma));

                    vec3 color = mix(original, posterized, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uLevels = GL.GetUniformLocation(_program, "uLevels");
            _uGamma = GL.GetUniformLocation(_program, "uGamma");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uLevels, _levels.CurrentValue);
            GL.Uniform1(_uGamma, _gamma.CurrentValue);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            host.DrawFullscreen(_program, inputTexture);
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }
        }
    }

    private sealed class RadialBlurEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.radialBlur";
        private readonly Parameter<float> _centerX = new("Radial Blur Effect / Center X", 0f, 1f, 0.5f);
        private readonly Parameter<float> _centerY = new("Radial Blur Effect / Center Y", 0f, 1f, 0.5f);
        private readonly Parameter<float> _strength = new("Radial Blur Effect / Strength", 0f, 8f, 1.5f);
        private readonly Parameter<int> _samples = new("Radial Blur Effect / Samples", 1, 48, 12);
        private readonly Parameter<float> _mix = new("Radial Blur Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uCenter;
        private int _uStrength;
        private int _uSamples;
        private int _uMix;

        public RadialBlurEffectStage()
        {
            _parameters = [_centerX, _centerY, _strength, _samples, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Radial Blur";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
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

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;

                uniform sampler2D uTexture;
                uniform vec2 uCenter;
                uniform float uStrength;
                uniform int uSamples;
                uniform float uMix;

                const int MAX_SAMPLES = 64;

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;
                    vec2 p = vUv - uCenter;
                    float radius = length(p);
                    float baseAngle = atan(p.y, p.x);

                    int sampleCount = clamp(uSamples, 1, MAX_SAMPLES);
                    vec3 acc = vec3(0.0);

                    for (int i = 0; i < MAX_SAMPLES; i++)
                    {
                        if (i >= sampleCount)
                        {
                            break;
                        }

                        float t = sampleCount <= 1 ? 0.0 : (float(i) / float(sampleCount - 1)) * 2.0 - 1.0;
                        float angle = baseAngle + t * uStrength * radius * 0.25;
                        vec2 uv = uCenter + vec2(cos(angle), sin(angle)) * radius;
                        acc += texture(uTexture, clamp(uv, vec2(0.0), vec2(1.0))).rgb;
                    }

                    vec3 blurred = acc / float(sampleCount);
                    vec3 color = mix(original, blurred, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uCenter = GL.GetUniformLocation(_program, "uCenter");
            _uStrength = GL.GetUniformLocation(_program, "uStrength");
            _uSamples = GL.GetUniformLocation(_program, "uSamples");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform2(_uCenter, _centerX.CurrentValue, _centerY.CurrentValue);
            GL.Uniform1(_uStrength, _strength.CurrentValue);
            GL.Uniform1(_uSamples, _samples.CurrentValue);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            host.DrawFullscreen(_program, inputTexture);
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }
        }
    }

    private sealed class ZoomBlurEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.zoomBlur";
        private readonly Parameter<float> _centerX = new("Zoom Blur Effect / Center X", 0f, 1f, 0.5f);
        private readonly Parameter<float> _centerY = new("Zoom Blur Effect / Center Y", 0f, 1f, 0.5f);
        private readonly Parameter<float> _strength = new("Zoom Blur Effect / Strength", 0f, 2f, 0.35f);
        private readonly Parameter<int> _samples = new("Zoom Blur Effect / Samples", 1, 48, 16);
        private readonly Parameter<float> _mix = new("Zoom Blur Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uCenter;
        private int _uStrength;
        private int _uSamples;
        private int _uMix;

        public ZoomBlurEffectStage()
        {
            _parameters = [_centerX, _centerY, _strength, _samples, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Zoom Blur";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
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

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;

                uniform sampler2D uTexture;
                uniform vec2 uCenter;
                uniform float uStrength;
                uniform int uSamples;
                uniform float uMix;

                const int MAX_SAMPLES = 64;

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;
                    vec2 dir = vUv - uCenter;

                    int sampleCount = clamp(uSamples, 1, MAX_SAMPLES);
                    vec3 acc = vec3(0.0);

                    for (int i = 0; i < MAX_SAMPLES; i++)
                    {
                        if (i >= sampleCount)
                        {
                            break;
                        }

                        float t = sampleCount <= 1 ? 0.0 : float(i) / float(sampleCount - 1);
                        vec2 uv = vUv - dir * t * uStrength;
                        acc += texture(uTexture, clamp(uv, vec2(0.0), vec2(1.0))).rgb;
                    }

                    vec3 blurred = acc / float(sampleCount);
                    vec3 color = mix(original, blurred, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uCenter = GL.GetUniformLocation(_program, "uCenter");
            _uStrength = GL.GetUniformLocation(_program, "uStrength");
            _uSamples = GL.GetUniformLocation(_program, "uSamples");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform2(_uCenter, _centerX.CurrentValue, _centerY.CurrentValue);
            GL.Uniform1(_uStrength, _strength.CurrentValue);
            GL.Uniform1(_uSamples, _samples.CurrentValue);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            host.DrawFullscreen(_program, inputTexture);
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }
        }
    }

    private sealed class MotionBlurEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.motionBlur";
        private readonly Parameter<float> _directionRadians = new("Motion Blur Effect / Direction (rad)", -6.28319f, 6.28319f, 0f);
        private readonly Parameter<float> _distancePixels = new("Motion Blur Effect / Distance (px)", 0f, 256f, 24f);
        private readonly Parameter<int> _samples = new("Motion Blur Effect / Samples", 1, 48, 12);
        private readonly Parameter<float> _mix = new("Motion Blur Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uDirectionRadians;
        private int _uDistancePixels;
        private int _uSamples;
        private int _uMix;

        public MotionBlurEffectStage()
        {
            _parameters = [_directionRadians, _distancePixels, _samples, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Motion Blur";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
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

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;

                uniform sampler2D uTexture;
                uniform float uDirectionRadians;
                uniform float uDistancePixels;
                uniform int uSamples;
                uniform float uMix;

                const int MAX_SAMPLES = 64;

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;

                    vec2 texSize = vec2(textureSize(uTexture, 0));
                    vec2 texel = 1.0 / max(texSize, vec2(1.0));
                    vec2 dir = vec2(cos(uDirectionRadians), sin(uDirectionRadians));
                    vec2 motion = dir * uDistancePixels * texel;

                    int sampleCount = clamp(uSamples, 1, MAX_SAMPLES);
                    vec3 acc = vec3(0.0);

                    for (int i = 0; i < MAX_SAMPLES; i++)
                    {
                        if (i >= sampleCount)
                        {
                            break;
                        }

                        float t = sampleCount <= 1 ? 0.0 : (float(i) / float(sampleCount - 1)) * 2.0 - 1.0;
                        vec2 uv = vUv + motion * t;
                        acc += texture(uTexture, clamp(uv, vec2(0.0), vec2(1.0))).rgb;
                    }

                    vec3 blurred = acc / float(sampleCount);
                    vec3 color = mix(original, blurred, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uDirectionRadians = GL.GetUniformLocation(_program, "uDirectionRadians");
            _uDistancePixels = GL.GetUniformLocation(_program, "uDistancePixels");
            _uSamples = GL.GetUniformLocation(_program, "uSamples");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uDirectionRadians, _directionRadians.CurrentValue);
            GL.Uniform1(_uDistancePixels, _distancePixels.CurrentValue);
            GL.Uniform1(_uSamples, _samples.CurrentValue);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            host.DrawFullscreen(_program, inputTexture);
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }
        }
    }

    private sealed class ColorSwapEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.colorSwap";
        private readonly Parameter<int> _mode = new("Color Swap Effect / Mode", 0, 5, 0);
        private readonly Parameter<float> _mix = new("Color Swap Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uMode;
        private int _uMix;

        public ColorSwapEffectStage()
        {
            _parameters = [_mode, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Color Swap";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
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

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;

                uniform sampler2D uTexture;
                uniform int uMode;
                uniform float uMix;

                vec3 swapChannels(vec3 c, int mode)
                {
                    if (mode == 1) return c.rbg;
                    if (mode == 2) return c.grb;
                    if (mode == 3) return c.gbr;
                    if (mode == 4) return c.brg;
                    if (mode == 5) return c.bgr;
                    return c.rgb;
                }

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;
                    int mode = clamp(uMode, 0, 5);
                    vec3 swapped = swapChannels(original, mode);
                    vec3 color = mix(original, swapped, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uMode = GL.GetUniformLocation(_program, "uMode");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uMode, _mode.CurrentValue);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            host.DrawFullscreen(_program, inputTexture);
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }
        }
    }

    private sealed class SnapshotPeakEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.snapshotPeak";
        private readonly Parameter<float> _snapshotSignal = new("Snapshot Effect / Signal", 0f, 2f, 0f);
        private readonly Parameter<float> _peakThreshold = new("Snapshot Effect / Peak Threshold", 0f, 2f, 0.8f);
        private readonly Parameter<float> _minPeakInterval = new("Snapshot Effect / Min Interval (s)", 0.02f, 2f, 0.2f);
        private readonly Parameter<float> _holdSeconds = new("Snapshot Effect / Hold Time (s)", 0.05f, 8f, 2.5f);
        private readonly Parameter<int> _snapshotCount = new("Snapshot Effect / Count", 1, MaxSnapshots, MaxSnapshots);
        private readonly Parameter<float> _snapshotOpacity = new("Snapshot Effect / Opacity", 0f, 1f, 1f);
        private readonly Parameter<float> _opacityDrop = new("Snapshot Effect / Opacity Drop", 0f, 0.95f, 0.2f);
        private readonly Parameter<float> _mix = new("Snapshot Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private readonly int[] _snapshotTextures = new int[MaxSnapshots];
        private readonly float[] _snapshotTimes = new float[MaxSnapshots];
        private readonly float[] _snapshotWeights = new float[MaxSnapshots];

        private int _writeIndex;
        private bool _signalInitialized;
        private float _signalPrev2;
        private float _signalPrev1;
        private float _lastCaptureTime = -10_000f;
        private int _lastWidth;
        private int _lastHeight;

        private int _program;
        private readonly int[] _uSnapshotSamplers = new int[MaxSnapshots];
        private int _uInput;
        private int _uWeights;
        private int _uMix;

        public SnapshotPeakEffectStage()
        {
            _parameters =
            [
                _snapshotSignal,
                _peakThreshold,
                _minPeakInterval,
                _holdSeconds,
                _snapshotCount,
                _snapshotOpacity,
                _opacityDrop,
                _mix
            ];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Snapshot Peak Hold";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
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

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;

                uniform sampler2D uInputTexture;
                uniform sampler2D uSnapshot0;
                uniform sampler2D uSnapshot1;
                uniform sampler2D uSnapshot2;
                uniform sampler2D uSnapshot3;
                uniform sampler2D uSnapshot4;
                uniform sampler2D uSnapshot5;
                uniform sampler2D uSnapshot6;
                uniform sampler2D uSnapshot7;
                uniform float uSnapshotWeights[8];
                uniform float uMix;

                vec3 snapshotAt(int index, vec2 uv)
                {
                    if (index == 0) return texture(uSnapshot0, uv).rgb;
                    if (index == 1) return texture(uSnapshot1, uv).rgb;
                    if (index == 2) return texture(uSnapshot2, uv).rgb;
                    if (index == 3) return texture(uSnapshot3, uv).rgb;
                    if (index == 4) return texture(uSnapshot4, uv).rgb;
                    if (index == 5) return texture(uSnapshot5, uv).rgb;
                    if (index == 6) return texture(uSnapshot6, uv).rgb;
                    return texture(uSnapshot7, uv).rgb;
                }

                void main()
                {
                    vec3 live = texture(uInputTexture, vUv).rgb;
                    vec3 composited = live;

                    for (int i = 0; i < 8; i++)
                    {
                        float w = clamp(uSnapshotWeights[i], 0.0, 1.0);
                        if (w <= 0.0001)
                        {
                            continue;
                        }

                        vec3 shot = snapshotAt(i, vUv);
                        composited = mix(composited, shot, w);
                    }

                    vec3 color = mix(live, composited, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uInput = GL.GetUniformLocation(_program, "uInputTexture");
            _uWeights = GL.GetUniformLocation(_program, "uSnapshotWeights");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            for (var i = 0; i < MaxSnapshots; i++)
            {
                _uSnapshotSamplers[i] = GL.GetUniformLocation(_program, $"uSnapshot{i}");
            }

            GL.UseProgram(_program);
            GL.Uniform1(_uInput, 0);
            for (var i = 0; i < MaxSnapshots; i++)
            {
                GL.Uniform1(_uSnapshotSamplers[i], i + 1);
            }
            GL.UseProgram(0);
        }

        public override void OnResize(int width, int height, VisualPipeline host)
        {
            if (width == _lastWidth && height == _lastHeight)
            {
                return;
            }

            _lastWidth = width;
            _lastHeight = height;

            for (var i = 0; i < MaxSnapshots; i++)
            {
                if (_snapshotTextures[i] != 0)
                {
                    GL.DeleteTexture(_snapshotTextures[i]);
                    _snapshotTextures[i] = 0;
                }

                _snapshotTextures[i] = CreateRenderTexture(width, height);
                _snapshotTimes[i] = -10_000f;
                _snapshotWeights[i] = 0f;
            }

            _writeIndex = 0;
            _signalInitialized = false;
            _lastCaptureTime = -10_000f;
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            UpdatePeakCapture(host, inputTexture, time);

            var hold = MathF.Max(0.01f, _holdSeconds.CurrentValue);
            var activeCount = Math.Clamp(_snapshotCount.CurrentValue, 1, MaxSnapshots);
            var opacity = Math.Clamp(_snapshotOpacity.CurrentValue, 0f, 1f);
            var drop = Math.Clamp(_opacityDrop.CurrentValue, 0f, 0.95f);

            for (var i = 0; i < MaxSnapshots; i++)
            {
                var sourceIndex = (_writeIndex - 1 - i + MaxSnapshots) % MaxSnapshots;
                var age = time - _snapshotTimes[sourceIndex];
                var holdWeight = age >= 0f && age <= hold ? 1f - (age / hold) : 0f;
                var rankWeight = MathF.Pow(1f - drop, i);
                _snapshotWeights[i] = i < activeCount ? holdWeight * rankWeight * opacity : 0f;
            }

            GL.UseProgram(_program);
            GL.Uniform1(_uWeights, MaxSnapshots, _snapshotWeights);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            var bindings = new (int TextureUnitIndex, int TextureId)[1 + MaxSnapshots];
            bindings[0] = (0, inputTexture);
            for (var i = 0; i < MaxSnapshots; i++)
            {
                var sourceIndex = (_writeIndex - 1 - i + MaxSnapshots) % MaxSnapshots;
                bindings[i + 1] = (1 + i, _snapshotTextures[sourceIndex]);
            }

            host.DrawFullscreenWithTextures(_program, bindings);
        }

        private void UpdatePeakCapture(VisualPipeline host, int inputTexture, float currentTime)
        {
            var signal = _snapshotSignal.CurrentValue;
            if (!_signalInitialized)
            {
                _signalPrev2 = signal;
                _signalPrev1 = signal;
                _signalInitialized = true;
                return;
            }

            var threshold = _peakThreshold.CurrentValue;
            var minInterval = MathF.Max(0.01f, _minPeakInterval.CurrentValue);

            var isLocalPeak = _signalPrev1 > _signalPrev2 && _signalPrev1 >= signal;
            var passesThreshold = _signalPrev1 >= threshold;
            var canCapture = currentTime - _lastCaptureTime >= minInterval;

            if (isLocalPeak && passesThreshold && canCapture)
            {
                host.CopyTexture(inputTexture, _snapshotTextures[_writeIndex]);
                _snapshotTimes[_writeIndex] = currentTime;
                _writeIndex = (_writeIndex + 1) % MaxSnapshots;
                _lastCaptureTime = currentTime;
            }

            _signalPrev2 = _signalPrev1;
            _signalPrev1 = signal;
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }

            for (var i = 0; i < MaxSnapshots; i++)
            {
                if (_snapshotTextures[i] != 0)
                {
                    GL.DeleteTexture(_snapshotTextures[i]);
                    _snapshotTextures[i] = 0;
                }
            }
        }
    }
}

public sealed class VisualPipelinePresetState
{
    public List<VisualPipelineStagePresetState> Stages { get; set; } = [];
}

public sealed class VisualPipelineStagePresetState
{
    public string StageTypeId { get; set; } = string.Empty;
    public string InputSource { get; set; } = "Previous";
    public Dictionary<string, float> ParameterValues { get; set; } = [];
}
