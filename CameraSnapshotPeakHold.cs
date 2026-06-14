using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed class CameraSnapshotPeakHold : IVisual
{
    private const int MaxSnapshots = 8;

    private readonly Parameter<float> _snapshotSignal = new("Snapshot Signal", 0f, 2f, 0f);
    private readonly Parameter<float> _peakThreshold = new("Peak Threshold", 0f, 2f, 0.8f);
    private readonly Parameter<float> _minPeakInterval = new("Min Peak Interval (s)", 0.02f, 2f, 0.2f);
    private readonly Parameter<float> _holdSeconds = new("Hold Time (s)", 0.05f, 8f, 2.5f);
    private readonly Parameter<int> _activeSnapshots = new("Snapshot Count", 1, MaxSnapshots, MaxSnapshots);
    private readonly Parameter<float> _snapshotOpacity = new("Snapshot Opacity", 0f, 1f, 0.85f);
    private readonly Parameter<float> _opacityDropPerShot = new("Opacity Drop / Shot", 0f, 0.95f, 0.2f);
    private readonly Parameter<float> _compositeMix = new("Composite Mix", 0f, 1f, 1f);

    private readonly IReadOnlyList<IParameter> _parameters;

    private CameraInput? _cameraInput;
    private List<int> _deviceIndices = [];
    private int _selectedDeviceIndex;
    private string _cameraStatus = "Not initialized";
    private bool _cameraReinitPending;

    private int _captureShader;
    private int _compositeShader;
    private int _vao;
    private int _vbo;
    private int _ebo;

    private int _captureFbo;
    private int _copyFbo;
    private int _liveTexture;
    private readonly int[] _snapshotTextures = new int[MaxSnapshots];
    private readonly float[] _snapshotTimestamps = new float[MaxSnapshots];
    private readonly float[] _snapshotWeights = new float[MaxSnapshots];

    private int _renderWidth;
    private int _renderHeight;
    private int _snapshotWriteIndex;

    private bool _signalInitialized;
    private float _signalPrev2;
    private float _signalPrev1;
    private float _lastCaptureTime = -10_000f;

    private int _uCaptureCameraTexture;

    private int _uCompositeLiveTexture;
    private readonly int[] _uCompositeSnapshots = new int[MaxSnapshots];
    private int _uCompositeSnapshotWeights;
    private int _uCompositeMix;

    public string Name => "Camera Snapshot Peaks";
    public IReadOnlyList<IParameter> Parameters => _parameters;

    public IReadOnlyList<int> AvailableDeviceIndices => _deviceIndices;
    public int SelectedDeviceIndex => _selectedDeviceIndex;
    public string CameraStatus => _cameraStatus;

    public CameraSnapshotPeakHold()
    {
        _parameters =
        [
            _snapshotSignal,
            _peakThreshold,
            _minPeakInterval,
            _holdSeconds,
            _activeSnapshots,
            _snapshotOpacity,
            _opacityDropPerShot,
            _compositeMix
        ];

        RefreshDevices();
        CreateGlResources();
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

    public void Render(float[] spectrum, float time)
    {
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.ScissorTest);

        HandlePendingCameraReinitialize();
        EnsureCameraInitialized();
        if (_cameraInput is null)
        {
            return;
        }

        _cameraInput.UpdateTextureFromLatestFrame();
        EnsureRenderTargets();
        if (_captureFbo == 0 || _liveTexture == 0)
        {
            return;
        }

        // Pass 1: render camera to live texture.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _captureFbo);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        GL.UseProgram(_captureShader);
        GL.BindVertexArray(_vao);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _cameraInput.TextureId);
        GL.Uniform1(_uCaptureCameraTexture, 0);
        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);

        UpdatePeakCapture(time);

        // Prepare snapshot weights and bind textures.
        var hold = MathF.Max(0.01f, _holdSeconds.CurrentValue);
        var activeCount = Math.Clamp(_activeSnapshots.CurrentValue, 1, MaxSnapshots);
        var opacity = Math.Clamp(_snapshotOpacity.CurrentValue, 0f, 1f);
        var drop = Math.Clamp(_opacityDropPerShot.CurrentValue, 0f, 0.95f);

        for (var i = 0; i < MaxSnapshots; i++)
        {
            var sourceIndex = (_snapshotWriteIndex - 1 - i + MaxSnapshots) % MaxSnapshots;
            var age = time - _snapshotTimestamps[sourceIndex];
            var holdWeight = age >= 0f && age <= hold ? 1f - (age / hold) : 0f;
            var rankWeight = MathF.Pow(1f - drop, i);
            _snapshotWeights[i] = i < activeCount ? holdWeight * rankWeight * opacity : 0f;

            GL.ActiveTexture(TextureUnit.Texture1 + i);
            GL.BindTexture(TextureTarget.Texture2D, _snapshotTextures[sourceIndex]);
        }

        // Pass 2: composite to screen.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);

        GL.UseProgram(_compositeShader);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _liveTexture);

        GL.Uniform1(_uCompositeLiveTexture, 0);
        GL.Uniform1(_uCompositeSnapshotWeights, MaxSnapshots, _snapshotWeights);
        GL.Uniform1(_uCompositeMix, _compositeMix.CurrentValue);

        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);

        for (var i = 0; i < MaxSnapshots; i++)
        {
            GL.ActiveTexture(TextureUnit.Texture1 + i);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
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

    private void EnsureRenderTargets()
    {
        var viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);

        var width = Math.Max(1, viewport[2]);
        var height = Math.Max(1, viewport[3]);

        if (width == _renderWidth && height == _renderHeight && _liveTexture != 0)
        {
            return;
        }

        _renderWidth = width;
        _renderHeight = height;

        if (_liveTexture != 0)
        {
            GL.DeleteTexture(_liveTexture);
            _liveTexture = 0;
        }

        for (var i = 0; i < _snapshotTextures.Length; i++)
        {
            if (_snapshotTextures[i] != 0)
            {
                GL.DeleteTexture(_snapshotTextures[i]);
                _snapshotTextures[i] = 0;
            }
        }

        _liveTexture = CreateRenderTexture(_renderWidth, _renderHeight);
        for (var i = 0; i < _snapshotTextures.Length; i++)
        {
            _snapshotTextures[i] = CreateRenderTexture(_renderWidth, _renderHeight);
        }

        if (_captureFbo == 0)
        {
            _captureFbo = GL.GenFramebuffer();
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _captureFbo);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _liveTexture,
            0);

        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
        {
            throw new InvalidOperationException($"CameraSnapshotPeakHold capture framebuffer incomplete: {status}");
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        if (_copyFbo == 0)
        {
            _copyFbo = GL.GenFramebuffer();
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _copyFbo);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        for (var i = 0; i < _snapshotTextures.Length; i++)
        {
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _snapshotTextures[i],
                0);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            _snapshotTimestamps[i] = -10_000f;
            _snapshotWeights[i] = 0f;
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _snapshotWriteIndex = 0;
        _signalInitialized = false;
        _lastCaptureTime = -10_000f;
    }

    private void UpdatePeakCapture(float currentTime)
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
            CaptureLiveFrameToSnapshot(currentTime);
            _lastCaptureTime = currentTime;
        }

        _signalPrev2 = _signalPrev1;
        _signalPrev1 = signal;
    }

    private void CaptureLiveFrameToSnapshot(float timestamp)
    {
        if (_captureFbo == 0 || _copyFbo == 0 || _snapshotTextures[_snapshotWriteIndex] == 0)
        {
            return;
        }

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _captureFbo);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _copyFbo);
        GL.FramebufferTexture2D(
            FramebufferTarget.DrawFramebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _snapshotTextures[_snapshotWriteIndex],
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

        _snapshotTimestamps[_snapshotWriteIndex] = timestamp;
        _snapshotWriteIndex = (_snapshotWriteIndex + 1) % MaxSnapshots;
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
        const string captureVertexSource = """
            #version 330 core
            layout (location = 0) in vec2 aPosition;
            layout (location = 1) in vec2 aUv;

            out vec2 vUv;

            void main()
            {
                vUv = vec2(aUv.x, 1.0 - aUv.y);
                gl_Position = vec4(aPosition, 0.0, 1.0);
            }
            """;

        const string captureFragmentSource = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColor;

            uniform sampler2D uCameraTexture;

            void main()
            {
                fragColor = texture(uCameraTexture, vUv);
            }
            """;

        const string compositeVertexSource = """
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

        const string compositeFragmentSource = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColor;

            uniform sampler2D uLiveTexture;
            uniform sampler2D uSnapshot0;
            uniform sampler2D uSnapshot1;
            uniform sampler2D uSnapshot2;
            uniform sampler2D uSnapshot3;
            uniform sampler2D uSnapshot4;
            uniform sampler2D uSnapshot5;
            uniform sampler2D uSnapshot6;
            uniform sampler2D uSnapshot7;
            uniform float uSnapshotWeights[8];
            uniform float uCompositeMix;

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
                vec3 live = texture(uLiveTexture, vUv).rgb;
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

                vec3 color = mix(live, composited, clamp(uCompositeMix, 0.0, 1.0));
                fragColor = vec4(color, 1.0);
            }
            """;

        _captureShader = CompileProgram(captureVertexSource, captureFragmentSource);
        _compositeShader = CompileProgram(compositeVertexSource, compositeFragmentSource);

        _uCaptureCameraTexture = GL.GetUniformLocation(_captureShader, "uCameraTexture");

        _uCompositeLiveTexture = GL.GetUniformLocation(_compositeShader, "uLiveTexture");
        for (var i = 0; i < MaxSnapshots; i++)
        {
            _uCompositeSnapshots[i] = GL.GetUniformLocation(_compositeShader, $"uSnapshot{i}");
        }

        _uCompositeSnapshotWeights = GL.GetUniformLocation(_compositeShader, "uSnapshotWeights");
        _uCompositeMix = GL.GetUniformLocation(_compositeShader, "uCompositeMix");

        GL.UseProgram(_captureShader);
        GL.Uniform1(_uCaptureCameraTexture, 0);

        GL.UseProgram(_compositeShader);
        GL.Uniform1(_uCompositeLiveTexture, 0);
        for (var i = 0; i < MaxSnapshots; i++)
        {
            GL.Uniform1(_uCompositeSnapshots[i], i + 1);
        }

        GL.UseProgram(0);

        var vertices = new float[]
        {
            -1f, -1f,   0f, 0f,
             1f, -1f,   1f, 0f,
             1f,  1f,   1f, 1f,
            -1f,  1f,   0f, 1f
        };

        var indices = new uint[] { 0, 1, 2, 2, 3, 0 };

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        _ebo = GL.GenBuffer();

        GL.BindVertexArray(_vao);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);

        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));

        GL.BindVertexArray(0);
    }

    private static int CompileProgram(string vertexSource, string fragmentSource)
    {
        var vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, vertexSource);
        GL.CompileShader(vs);
        GL.GetShader(vs, ShaderParameter.CompileStatus, out var vsOk);
        if (vsOk == 0)
        {
            throw new InvalidOperationException($"CameraSnapshotPeakHold vertex shader compile failed: {GL.GetShaderInfoLog(vs)}");
        }

        var fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fragmentSource);
        GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out var fsOk);
        if (fsOk == 0)
        {
            throw new InvalidOperationException($"CameraSnapshotPeakHold fragment shader compile failed: {GL.GetShaderInfoLog(fs)}");
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linkOk);
        if (linkOk == 0)
        {
            throw new InvalidOperationException($"CameraSnapshotPeakHold shader link failed: {GL.GetProgramInfoLog(program)}");
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

        if (_captureFbo != 0) GL.DeleteFramebuffer(_captureFbo);
        if (_copyFbo != 0) GL.DeleteFramebuffer(_copyFbo);
        if (_liveTexture != 0) GL.DeleteTexture(_liveTexture);

        for (var i = 0; i < _snapshotTextures.Length; i++)
        {
            if (_snapshotTextures[i] != 0)
            {
                GL.DeleteTexture(_snapshotTextures[i]);
                _snapshotTextures[i] = 0;
            }
        }

        if (_ebo != 0) GL.DeleteBuffer(_ebo);
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_captureShader != 0) GL.DeleteProgram(_captureShader);
        if (_compositeShader != 0) GL.DeleteProgram(_compositeShader);
    }
}
