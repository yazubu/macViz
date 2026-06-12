using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed class CameraFilterEdgeDetection : IVisual, ICameraVisual
{
    private const int MaxHistoryFrames = 12;
    private const int MaxPeakTrailShots = 3;

    private readonly Parameter<float> _edgeStrength = new("Edge Strength", 0f, 5f, 1.5f);
    private readonly Parameter<float> _threshold = new("Threshold", 0f, 1f, 0.2f);
    private readonly Parameter<float> _mix = new("Mix", 0f, 1f, 1f);
    private readonly Parameter<float> _invert = new("Invert", 0f, 1f, 0f);
    private readonly Parameter<float> _colorPulse = new("Color Pulse", 0f, 1f, 0.75f);
    private readonly Parameter<float> _pulseSpeed = new("Pulse Speed", 0f, 10f, 2.5f);
    private readonly Parameter<float> _baseHue = new("Base Hue", 0f, 1f, 0.58f);
    private readonly Parameter<float> _colorSaturation = new("Color Saturation", 0f, 1f, 0.9f);
    private readonly Parameter<float> _scaleIn = new("Scale In", 0.2f, 3f, 1f);
    private readonly Parameter<float> _scaleOut = new("Scale Out", 0.2f, 3f, 1f);
    private readonly Parameter<int> _historyFrames = new("Previous Frames", 0, MaxHistoryFrames, 2);
    private readonly Parameter<float> _historyFade = new("History Fade", 0f, 0.98f, 0.72f);
    private readonly Parameter<float> _historyMultiply = new("History Multiply", 0f, 1f, 0.35f);
    private readonly Parameter<float> _trailStrength = new("Trail Strength", 0f, 2f, 0.75f);
    private readonly Parameter<float> _trailThreshold = new("Trail Threshold", 0f, 1f, 0.14f);
    private readonly Parameter<int> _trailStepFrames = new("Trail Step Frames", 1, MaxHistoryFrames, 2);
    private readonly Parameter<float> _trailPeakSignal = new("Trail Peak Signal", 0f, 2f, 0f);
    private readonly Parameter<float> _trailPeakThreshold = new("Trail Peak Threshold", 0f, 2f, 0.8f);
    private readonly Parameter<float> _trailHoldSeconds = new("Trail Hold (s)", 0.05f, 5f, 1.5f);
    private readonly Parameter<float> _trailMinPeakInterval = new("Trail Min Peak Interval (s)", 0.02f, 2f, 0.2f);

    private readonly IReadOnlyList<IParameter> _parameters;

    private CameraInput? _cameraInput;
    private List<int> _deviceIndices = [];
    private int _selectedDeviceIndex;
    private string _cameraStatus = "Not initialized";

    private int _effectShader;
    private int _compositeShader;
    private int _vao;
    private int _vbo;
    private int _ebo;

    private int _effectFbo;
    private int _copyFbo;
    private int _effectTexture;
    private readonly int[] _historyTextures = new int[MaxHistoryFrames];
    private readonly float[] _historyTimestamps = new float[MaxHistoryFrames];
    private readonly float[] _historyWeights = new float[MaxHistoryFrames];
    private readonly int[] _peakTrailTextures = new int[MaxPeakTrailShots];
    private readonly float[] _peakTrailTimestamps = new float[MaxPeakTrailShots];
    private readonly float[] _peakTrailWeights = new float[MaxPeakTrailShots];
    private int _historyWriteIndex;
    private int _peakTrailWriteIndex;
    private int _renderWidth;
    private int _renderHeight;
    private float _lastRenderTime = -1f;
    private float _estimatedFrameDelta = 1f / 60f;
    private float _lastPeakCaptureTime = -10_000f;
    private bool _peakSignalInitialized;
    private float _peakSignalPrev2;
    private float _peakSignalPrev1;

    private int _uCameraTexture;
    private int _uEdgeStrength;
    private int _uThreshold;
    private int _uMix;
    private int _uInvert;
    private int _uTime;
    private int _uColorPulse;
    private int _uPulseSpeed;
    private int _uBaseHue;
    private int _uColorSaturation;
    private int _uScaleIn;
    private int _uScaleOut;

    private int _uCurrentTexture;
    private readonly int[] _uHistorySamplers = new int[MaxHistoryFrames];
    private int _uHistoryMultiply;
    private int _uHistoryWeights;
    private int _uTrailStrength;
    private int _uTrailThreshold;
    private int _uTrailStepFrames;
    private readonly int[] _uPeakTrailSamplers = new int[MaxPeakTrailShots];
    private int _uPeakTrailWeights;

    public string Name => "Camera Filter (Edge Detection)";
    public IReadOnlyList<IParameter> Parameters => _parameters;

    public IReadOnlyList<int> AvailableDeviceIndices => _deviceIndices;
    public int SelectedDeviceIndex => _selectedDeviceIndex;
    public string CameraStatus => _cameraStatus;

    public CameraFilterEdgeDetection()
    {
        _parameters =
        [
            _edgeStrength,
            _threshold,
            _mix,
            _invert,
            _colorPulse,
            _pulseSpeed,
            _baseHue,
            _colorSaturation,
            _scaleIn,
            _scaleOut,
            _historyFrames,
            _historyFade,
            _historyMultiply,
            _trailStrength,
            _trailThreshold,
            _trailStepFrames,
            _trailPeakSignal,
            _trailPeakThreshold,
            _trailHoldSeconds,
            _trailMinPeakInterval
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
        }
    }

    public void SetSelectedDeviceIndex(int deviceIndex)
    {
        if (_selectedDeviceIndex == deviceIndex)
        {
            return;
        }

        _selectedDeviceIndex = deviceIndex;
        ReinitializeCamera();
    }

    public void Render(float[] spectrum, float time)
    {
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.ScissorTest);

        EnsureCameraInitialized();
        if (_cameraInput is null)
        {
            return;
        }

        _cameraInput.UpdateTextureFromLatestFrame();

        EnsureRenderTargets();
        if (_effectFbo == 0 || _effectTexture == 0)
        {
            return;
        }

        // Pass 1: render edge effect into offscreen texture.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _effectFbo);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        GL.UseProgram(_effectShader);
        GL.BindVertexArray(_vao);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _cameraInput.TextureId);

        GL.Uniform1(_uCameraTexture, 0);
        GL.Uniform1(_uEdgeStrength, _edgeStrength.CurrentValue);
        GL.Uniform1(_uThreshold, _threshold.CurrentValue);
        GL.Uniform1(_uMix, _mix.CurrentValue);
        GL.Uniform1(_uInvert, _invert.CurrentValue);
        GL.Uniform1(_uTime, time);
        GL.Uniform1(_uColorPulse, _colorPulse.CurrentValue);
        GL.Uniform1(_uPulseSpeed, _pulseSpeed.CurrentValue);
        GL.Uniform1(_uBaseHue, _baseHue.CurrentValue);
        GL.Uniform1(_uColorSaturation, _colorSaturation.CurrentValue);
        GL.Uniform1(_uScaleIn, _scaleIn.CurrentValue);
        GL.Uniform1(_uScaleOut, _scaleOut.CurrentValue);

        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);

        // Pass 2: composite current frame with history to screen.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);

        GL.UseProgram(_compositeShader);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _effectTexture);

        var currentTime = time;
        var frameDt = _lastRenderTime < 0f ? _estimatedFrameDelta : MathF.Max(1f / 240f, currentTime - _lastRenderTime);
        _lastRenderTime = currentTime;
        _estimatedFrameDelta = (_estimatedFrameDelta * 0.9f) + (frameDt * 0.1f);

        var historyWindowSeconds = Math.Clamp(_historyFrames.CurrentValue, 0, MaxHistoryFrames) * _estimatedFrameDelta;
        var fadePerSecond = Math.Clamp(_historyFade.CurrentValue, 0f, 0.999f);

        UpdatePeakTrailCaptures(currentTime);

        for (var i = 0; i < MaxHistoryFrames; i++)
        {
            var sourceIndex = (_historyWriteIndex - 1 - i + MaxHistoryFrames) % MaxHistoryFrames;
            GL.ActiveTexture(TextureUnit.Texture1 + i);
            GL.BindTexture(TextureTarget.Texture2D, _historyTextures[sourceIndex]);

            var age = currentTime - _historyTimestamps[sourceIndex];
            var inWindow = historyWindowSeconds > 0f && age > 0f && age <= historyWindowSeconds;
            _historyWeights[i] = inWindow ? MathF.Pow(fadePerSecond, age) : 0f;
        }

        var holdSeconds = MathF.Max(0.01f, _trailHoldSeconds.CurrentValue);
        for (var i = 0; i < MaxPeakTrailShots; i++)
        {
            var sourceIndex = (_peakTrailWriteIndex - 1 - i + MaxPeakTrailShots) % MaxPeakTrailShots;
            GL.ActiveTexture(TextureUnit.Texture1 + MaxHistoryFrames + i);
            GL.BindTexture(TextureTarget.Texture2D, _peakTrailTextures[sourceIndex]);

            var age = currentTime - _peakTrailTimestamps[sourceIndex];
            _peakTrailWeights[i] = age >= 0f && age <= holdSeconds ? 1f - (age / holdSeconds) : 0f;
        }

        GL.Uniform1(_uCurrentTexture, 0);
        GL.Uniform1(_uHistoryMultiply, _historyMultiply.CurrentValue);
        GL.Uniform1(_uTrailStrength, _trailStrength.CurrentValue);
        GL.Uniform1(_uTrailThreshold, _trailThreshold.CurrentValue);
        GL.Uniform1(_uTrailStepFrames, Math.Clamp(_trailStepFrames.CurrentValue, 1, MaxHistoryFrames));
        GL.Uniform1(_uHistoryWeights, MaxHistoryFrames, _historyWeights);
        GL.Uniform1(_uPeakTrailWeights, MaxPeakTrailShots, _peakTrailWeights);

        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);

        PushCurrentFrameToHistory(currentTime);

        for (var i = 0; i < MaxHistoryFrames; i++)
        {
            GL.ActiveTexture(TextureUnit.Texture1 + i);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        for (var i = 0; i < MaxPeakTrailShots; i++)
        {
            GL.ActiveTexture(TextureUnit.Texture1 + MaxHistoryFrames + i);
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

    private void ReinitializeCamera()
    {
        _cameraInput?.Dispose();
        _cameraInput = null;
        _cameraStatus = $"Reinitializing device {_selectedDeviceIndex}...";
    }

    private void EnsureRenderTargets()
    {
        var viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);

        var width = Math.Max(1, viewport[2]);
        var height = Math.Max(1, viewport[3]);

        if (width == _renderWidth && height == _renderHeight && _effectTexture != 0)
        {
            return;
        }

        _renderWidth = width;
        _renderHeight = height;

        if (_effectTexture != 0)
        {
            GL.DeleteTexture(_effectTexture);
            _effectTexture = 0;
        }

        for (var i = 0; i < _historyTextures.Length; i++)
        {
            if (_historyTextures[i] != 0)
            {
                GL.DeleteTexture(_historyTextures[i]);
                _historyTextures[i] = 0;
            }
        }

        for (var i = 0; i < _peakTrailTextures.Length; i++)
        {
            if (_peakTrailTextures[i] != 0)
            {
                GL.DeleteTexture(_peakTrailTextures[i]);
                _peakTrailTextures[i] = 0;
            }
        }

        _effectTexture = CreateRenderTexture(_renderWidth, _renderHeight);
        for (var i = 0; i < _historyTextures.Length; i++)
        {
            _historyTextures[i] = CreateRenderTexture(_renderWidth, _renderHeight);
        }

        for (var i = 0; i < _peakTrailTextures.Length; i++)
        {
            _peakTrailTextures[i] = CreateRenderTexture(_renderWidth, _renderHeight);
        }

        if (_effectFbo == 0)
        {
            _effectFbo = GL.GenFramebuffer();
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _effectFbo);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _effectTexture,
            0);

        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
        {
            throw new InvalidOperationException($"Edge detection framebuffer incomplete: {status}");
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        if (_copyFbo == 0)
        {
            _copyFbo = GL.GenFramebuffer();
        }

        // Clear history textures.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _copyFbo);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        for (var i = 0; i < _historyTextures.Length; i++)
        {
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _historyTextures[i],
                0);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            _historyTimestamps[i] = -10_000f;
        }

        for (var i = 0; i < _peakTrailTextures.Length; i++)
        {
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _peakTrailTextures[i],
                0);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            _peakTrailTimestamps[i] = -10_000f;
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _historyWriteIndex = 0;
        _peakTrailWriteIndex = 0;
        _lastRenderTime = -1f;
        _lastPeakCaptureTime = -10_000f;
        _peakSignalInitialized = false;
    }

    private void PushCurrentFrameToHistory(float timestamp)
    {
        if (_effectFbo == 0 || _copyFbo == 0 || _historyTextures[_historyWriteIndex] == 0)
        {
            return;
        }

        CopyEffectToTargetTexture(_historyTextures[_historyWriteIndex]);

        _historyTimestamps[_historyWriteIndex] = timestamp;
        _historyWriteIndex = (_historyWriteIndex + 1) % MaxHistoryFrames;
    }

    private void UpdatePeakTrailCaptures(float currentTime)
    {
        var signal = _trailPeakSignal.CurrentValue;
        if (!_peakSignalInitialized)
        {
            _peakSignalPrev2 = signal;
            _peakSignalPrev1 = signal;
            _peakSignalInitialized = true;
            return;
        }

        var threshold = _trailPeakThreshold.CurrentValue;
        var minInterval = MathF.Max(0.01f, _trailMinPeakInterval.CurrentValue);

        var isLocalPeak = _peakSignalPrev1 > _peakSignalPrev2 && _peakSignalPrev1 >= signal;
        var passesThreshold = _peakSignalPrev1 >= threshold;
        var canCapture = currentTime - _lastPeakCaptureTime >= minInterval;

        if (isLocalPeak && passesThreshold && canCapture)
        {
            PushCurrentFrameToPeakTrail(currentTime);
            _lastPeakCaptureTime = currentTime;
        }

        _peakSignalPrev2 = _peakSignalPrev1;
        _peakSignalPrev1 = signal;
    }

    private void PushCurrentFrameToPeakTrail(float timestamp)
    {
        if (_effectFbo == 0 || _copyFbo == 0 || _peakTrailTextures[_peakTrailWriteIndex] == 0)
        {
            return;
        }

        CopyEffectToTargetTexture(_peakTrailTextures[_peakTrailWriteIndex]);
        _peakTrailTimestamps[_peakTrailWriteIndex] = timestamp;
        _peakTrailWriteIndex = (_peakTrailWriteIndex + 1) % MaxPeakTrailShots;
    }

    private void CopyEffectToTargetTexture(int targetTexture)
    {
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _effectFbo);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _copyFbo);
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
        const string effectVertexSource = """
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

        const string effectFragmentSource = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColor;

            uniform sampler2D uCameraTexture;
            uniform float uEdgeStrength;
            uniform float uThreshold;
            uniform float uMix;
            uniform float uInvert;
            uniform float uTime;
            uniform float uColorPulse;
            uniform float uPulseSpeed;
            uniform float uBaseHue;
            uniform float uColorSaturation;
            uniform float uScaleIn;
            uniform float uScaleOut;

            vec2 scaledUv(vec2 uv)
            {
                float scale = max(uScaleIn, 0.001) / max(uScaleOut, 0.001);
                return ((uv - 0.5) / max(scale, 0.001)) + 0.5;
            }

            vec3 sampleCamera(vec2 uv)
            {
                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
                {
                    return vec3(0.0);
                }

                return texture(uCameraTexture, uv).rgb;
            }

            float lumaAt(vec2 uv)
            {
                vec3 c = sampleCamera(uv);
                return dot(c, vec3(0.299, 0.587, 0.114));
            }

            vec3 hsv2rgb(vec3 c)
            {
                vec3 p = abs(fract(c.xxx + vec3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
                return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
            }

            void main()
            {
                vec2 uv = scaledUv(vUv);
                vec3 original = sampleCamera(uv);

                vec2 texSize = vec2(textureSize(uCameraTexture, 0));
                vec2 texel = 1.0 / max(texSize, vec2(1.0));

                float tl = lumaAt(uv + vec2(-texel.x,  texel.y));
                float tc = lumaAt(uv + vec2( 0.0,      texel.y));
                float tr = lumaAt(uv + vec2( texel.x,  texel.y));
                float ml = lumaAt(uv + vec2(-texel.x,  0.0));
                float mr = lumaAt(uv + vec2( texel.x,  0.0));
                float bl = lumaAt(uv + vec2(-texel.x, -texel.y));
                float bc = lumaAt(uv + vec2( 0.0,     -texel.y));
                float br = lumaAt(uv + vec2( texel.x, -texel.y));

                float gx = -tl + tr - 2.0 * ml + 2.0 * mr - bl + br;
                float gy =  tl + 2.0 * tc + tr - bl - 2.0 * bc - br;

                float edge = length(vec2(gx, gy));
                edge *= max(uEdgeStrength, 0.0);

                float t = clamp(uThreshold, 0.0, 1.0);
                float edgeMask = smoothstep(t, min(1.0, t + 0.25), edge);
                edgeMask = mix(edgeMask, 1.0 - edgeMask, clamp(uInvert, 0.0, 1.0));

                float pulse = 0.5 + 0.5 * sin((uTime * max(uPulseSpeed, 0.0)) + (edgeMask * 6.28318));
                float pulseAmount = clamp(uColorPulse, 0.0, 1.0);
                float hue = fract(uBaseHue + (pulse * 0.2 * pulseAmount));
                float sat = clamp(uColorSaturation, 0.0, 1.0);
                float val = mix(0.5, 1.0, pulse);
                vec3 pulseColor = hsv2rgb(vec3(hue, sat, val));

                vec3 monochromeEdge = vec3(edgeMask);
                vec3 coloredEdge = pulseColor * edgeMask;
                vec3 edgeColor = mix(monochromeEdge, coloredEdge, pulseAmount);

                vec3 finalColor = mix(original, edgeColor, clamp(uMix, 0.0, 1.0));
                fragColor = vec4(finalColor, 1.0);
            }
            """;

        const string compositeFragmentSource = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColor;

            uniform sampler2D uCurrentTexture;
            uniform sampler2D uHistory0;
            uniform sampler2D uHistory1;
            uniform sampler2D uHistory2;
            uniform sampler2D uHistory3;
            uniform sampler2D uHistory4;
            uniform sampler2D uHistory5;
            uniform sampler2D uHistory6;
            uniform sampler2D uHistory7;
            uniform sampler2D uHistory8;
            uniform sampler2D uHistory9;
            uniform sampler2D uHistory10;
            uniform sampler2D uHistory11;
            uniform sampler2D uPeakTrail0;
            uniform sampler2D uPeakTrail1;
            uniform sampler2D uPeakTrail2;
            uniform float uHistoryMultiply;
            uniform float uTrailStrength;
            uniform float uTrailThreshold;
            uniform int uTrailStepFrames;
            uniform float uHistoryWeights[12];
            uniform float uPeakTrailWeights[3];

            vec3 historyAt(int index, vec2 uv)
            {
                if (index == 0) return texture(uHistory0, uv).rgb;
                if (index == 1) return texture(uHistory1, uv).rgb;
                if (index == 2) return texture(uHistory2, uv).rgb;
                if (index == 3) return texture(uHistory3, uv).rgb;
                if (index == 4) return texture(uHistory4, uv).rgb;
                if (index == 5) return texture(uHistory5, uv).rgb;
                if (index == 6) return texture(uHistory6, uv).rgb;
                if (index == 7) return texture(uHistory7, uv).rgb;
                if (index == 8) return texture(uHistory8, uv).rgb;
                if (index == 9) return texture(uHistory9, uv).rgb;
                if (index == 10) return texture(uHistory10, uv).rgb;
                return texture(uHistory11, uv).rgb;
            }

            vec3 peakTrailAt(int index, vec2 uv)
            {
                if (index == 0) return texture(uPeakTrail0, uv).rgb;
                if (index == 1) return texture(uPeakTrail1, uv).rgb;
                return texture(uPeakTrail2, uv).rgb;
            }

            void main()
            {
                vec3 current = texture(uCurrentTexture, vUv).rgb;
                vec3 accumulated = current;

                float multiplyAmount = clamp(uHistoryMultiply, 0.0, 1.0);
                float trailStrength = max(uTrailStrength, 0.0);
                float trailThreshold = clamp(uTrailThreshold, 0.0, 1.0);
                int trailStep = max(uTrailStepFrames, 1);

                float mulAcc = 1.0;
                vec3 trail = vec3(0.0);

                for (int i = 0; i < 12; i++)
                {
                    float fade = clamp(uHistoryWeights[i], 0.0, 1.0);
                    if (fade <= 0.0001)
                    {
                        continue;
                    }

                    vec3 prev = historyAt(i, vUv);
                    accumulated += prev * fade;

                    float prevLum = dot(prev, vec3(0.2126, 0.7152, 0.0722));
                    float mulTerm = mix(1.0, max(prevLum, 0.001), fade * multiplyAmount);
                    mulAcc *= mulTerm;

                    if ((i % trailStep) == 0)
                    {
                        float motion = length(current - prev);
                        float edgePresence = dot(prev, vec3(0.2126, 0.7152, 0.0722));
                        float gate = smoothstep(trailThreshold, min(1.0, trailThreshold + 0.2), motion * edgePresence);
                        trail += prev * gate * fade;
                    }
                }

                for (int i = 0; i < 3; i++)
                {
                    float holdWeight = clamp(uPeakTrailWeights[i], 0.0, 1.0);
                    if (holdWeight <= 0.0001)
                    {
                        continue;
                    }

                    vec3 shot = peakTrailAt(i, vUv);
                    float shotEdge = dot(shot, vec3(0.2126, 0.7152, 0.0722));
                    float gate = smoothstep(trailThreshold, min(1.0, trailThreshold + 0.2), shotEdge);
                    trail += shot * gate * holdWeight;
                }

                vec3 color = (accumulated * mulAcc) + (trail * trailStrength);
                fragColor = vec4(color, 1.0);
            }
            """;

        _effectShader = CompileProgram(effectVertexSource, effectFragmentSource);
        _compositeShader = CompileProgram(compositeVertexSource, compositeFragmentSource);

        _uCameraTexture = GL.GetUniformLocation(_effectShader, "uCameraTexture");
        _uEdgeStrength = GL.GetUniformLocation(_effectShader, "uEdgeStrength");
        _uThreshold = GL.GetUniformLocation(_effectShader, "uThreshold");
        _uMix = GL.GetUniformLocation(_effectShader, "uMix");
        _uInvert = GL.GetUniformLocation(_effectShader, "uInvert");
        _uTime = GL.GetUniformLocation(_effectShader, "uTime");
        _uColorPulse = GL.GetUniformLocation(_effectShader, "uColorPulse");
        _uPulseSpeed = GL.GetUniformLocation(_effectShader, "uPulseSpeed");
        _uBaseHue = GL.GetUniformLocation(_effectShader, "uBaseHue");
        _uColorSaturation = GL.GetUniformLocation(_effectShader, "uColorSaturation");
        _uScaleIn = GL.GetUniformLocation(_effectShader, "uScaleIn");
        _uScaleOut = GL.GetUniformLocation(_effectShader, "uScaleOut");

        _uCurrentTexture = GL.GetUniformLocation(_compositeShader, "uCurrentTexture");
        for (var i = 0; i < MaxHistoryFrames; i++)
        {
            _uHistorySamplers[i] = GL.GetUniformLocation(_compositeShader, $"uHistory{i}");
        }

        _uHistoryMultiply = GL.GetUniformLocation(_compositeShader, "uHistoryMultiply");
        _uTrailStrength = GL.GetUniformLocation(_compositeShader, "uTrailStrength");
        _uTrailThreshold = GL.GetUniformLocation(_compositeShader, "uTrailThreshold");
        _uTrailStepFrames = GL.GetUniformLocation(_compositeShader, "uTrailStepFrames");
        _uHistoryWeights = GL.GetUniformLocation(_compositeShader, "uHistoryWeights");
        _uPeakTrailWeights = GL.GetUniformLocation(_compositeShader, "uPeakTrailWeights");

        for (var i = 0; i < MaxPeakTrailShots; i++)
        {
            _uPeakTrailSamplers[i] = GL.GetUniformLocation(_compositeShader, $"uPeakTrail{i}");
        }

        GL.UseProgram(_compositeShader);
        GL.Uniform1(_uCurrentTexture, 0);
        for (var i = 0; i < MaxHistoryFrames; i++)
        {
            GL.Uniform1(_uHistorySamplers[i], i + 1);
        }

        for (var i = 0; i < MaxPeakTrailShots; i++)
        {
            GL.Uniform1(_uPeakTrailSamplers[i], 1 + MaxHistoryFrames + i);
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
            throw new InvalidOperationException($"CameraFilterEdgeDetection vertex shader compile failed: {GL.GetShaderInfoLog(vs)}");
        }

        var fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fragmentSource);
        GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out var fsOk);
        if (fsOk == 0)
        {
            throw new InvalidOperationException($"CameraFilterEdgeDetection fragment shader compile failed: {GL.GetShaderInfoLog(fs)}");
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linkOk);
        if (linkOk == 0)
        {
            throw new InvalidOperationException($"CameraFilterEdgeDetection shader link failed: {GL.GetProgramInfoLog(program)}");
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

        if (_copyFbo != 0) GL.DeleteFramebuffer(_copyFbo);
        if (_effectFbo != 0) GL.DeleteFramebuffer(_effectFbo);
        if (_effectTexture != 0) GL.DeleteTexture(_effectTexture);

        for (var i = 0; i < _historyTextures.Length; i++)
        {
            if (_historyTextures[i] != 0)
            {
                GL.DeleteTexture(_historyTextures[i]);
                _historyTextures[i] = 0;
            }
        }

        for (var i = 0; i < _peakTrailTextures.Length; i++)
        {
            if (_peakTrailTextures[i] != 0)
            {
                GL.DeleteTexture(_peakTrailTextures[i]);
                _peakTrailTextures[i] = 0;
            }
        }

        if (_ebo != 0) GL.DeleteBuffer(_ebo);
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_effectShader != 0) GL.DeleteProgram(_effectShader);
        if (_compositeShader != 0) GL.DeleteProgram(_compositeShader);
    }
}
