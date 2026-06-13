using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed class CameraFilterEdgeDetection : IVisual, ICameraVisual
{
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

    private readonly IReadOnlyList<IParameter> _parameters;

    private CameraInput? _cameraInput;
    private List<int> _deviceIndices = [];
    private int _selectedDeviceIndex;
    private string _cameraStatus = "Not initialized";
    private bool _cameraReinitPending;

    private int _shader;
    private int _vao;
    private int _vbo;
    private int _ebo;

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
            _scaleOut
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

        GL.UseProgram(_shader);
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

    private void CreateGlResources()
    {
        const string vertexSource = """
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

        const string fragmentSource = """
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

        _shader = CompileProgram(vertexSource, fragmentSource);

        _uCameraTexture = GL.GetUniformLocation(_shader, "uCameraTexture");
        _uEdgeStrength = GL.GetUniformLocation(_shader, "uEdgeStrength");
        _uThreshold = GL.GetUniformLocation(_shader, "uThreshold");
        _uMix = GL.GetUniformLocation(_shader, "uMix");
        _uInvert = GL.GetUniformLocation(_shader, "uInvert");
        _uTime = GL.GetUniformLocation(_shader, "uTime");
        _uColorPulse = GL.GetUniformLocation(_shader, "uColorPulse");
        _uPulseSpeed = GL.GetUniformLocation(_shader, "uPulseSpeed");
        _uBaseHue = GL.GetUniformLocation(_shader, "uBaseHue");
        _uColorSaturation = GL.GetUniformLocation(_shader, "uColorSaturation");
        _uScaleIn = GL.GetUniformLocation(_shader, "uScaleIn");
        _uScaleOut = GL.GetUniformLocation(_shader, "uScaleOut");

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

        if (_ebo != 0) GL.DeleteBuffer(_ebo);
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_shader != 0) GL.DeleteProgram(_shader);
    }
}
