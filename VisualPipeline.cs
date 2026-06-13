using ImGuiNET;
using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline : IVisual, ICameraVisual, IVisualEditorPanel
{
    private const int MaxSnapshots = 8;

    private readonly List<PipelineStage> _stages = [];
    private readonly Queue<PipelineStage> _pendingDisposals = new();
    private readonly List<IParameter> _parameters = [];

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
        new(RadialWavesEffectStage.TypeIdValue, "Radial Waves Effect", () => new RadialWavesEffectStage()),
        new(RadialBlurEffectStage.TypeIdValue, "Radial Blur Effect", () => new RadialBlurEffectStage()),
        new(ZoomBlurEffectStage.TypeIdValue, "Zoom Blur Effect", () => new ZoomBlurEffectStage()),
        new(MotionBlurEffectStage.TypeIdValue, "Motion Blur Effect", () => new MotionBlurEffectStage()),
        new(MotionIsolateEffectStage.TypeIdValue, "Motion Isolate Effect", () => new MotionIsolateEffectStage()),
        new(ChromaticSmearEffectStage.TypeIdValue, "Chromatic Smear Effect", () => new ChromaticSmearEffectStage()),
        new(CubistEchoesEffectStage.TypeIdValue, "Cubist Echoes Effect", () => new CubistEchoesEffectStage()),
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

            if (stage.RefreshDynamicParameters())
            {
                foreach (var parameter in stage.Parameters)
                {
                    if (stageState.ParameterValues.TryGetValue(parameter.Name, out var numericValue))
                    {
                        SetParameterNumericValue(parameter, numericValue);
                    }
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

        HandlePendingCameraReinitialize();
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
        foreach (var stage in _stages)
        {
            if (stage.RefreshDynamicParameters())
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
