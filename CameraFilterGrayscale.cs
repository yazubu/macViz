using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed class CameraFilterGrayscale : IVisual, ICameraVisual
{
    private readonly Parameter<float> _intensity = new("Intensity", 0f, 1f, 1f);
    private readonly Parameter<float> _threshold = new("Threshold", 0f, 1f, 0.45f);
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
    private int _uIntensity;
    private int _uThreshold;

    public string Name => "Camera Filter (Grayscale)";
    public IReadOnlyList<IParameter> Parameters => _parameters;

    public IReadOnlyList<int> AvailableDeviceIndices => _deviceIndices;
    public int SelectedDeviceIndex => _selectedDeviceIndex;
    public string CameraStatus => _cameraStatus;

    public CameraFilterGrayscale()
    {
        _parameters = [_intensity, _threshold];
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
        GL.Uniform1(_uIntensity, _intensity.CurrentValue);
        GL.Uniform1(_uThreshold, _threshold.CurrentValue);

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
            uniform float uIntensity;
            uniform float uThreshold;

            void main()
            {
                vec3 color = texture(uCameraTexture, vUv).rgb;
                float luma = dot(color, vec3(0.299, 0.587, 0.114));

                float edge = smoothstep(max(0.0, uThreshold - 0.1), min(1.0, uThreshold + 0.1), luma);
                vec3 gray = vec3(edge);

                vec3 finalColor = mix(color, gray, clamp(uIntensity, 0.0, 1.0));
                fragColor = vec4(finalColor, 1.0);
            }
            """;

        _shader = CompileProgram(vertexSource, fragmentSource);

        _uCameraTexture = GL.GetUniformLocation(_shader, "uCameraTexture");
        _uIntensity = GL.GetUniformLocation(_shader, "uIntensity");
        _uThreshold = GL.GetUniformLocation(_shader, "uThreshold");

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
            throw new InvalidOperationException($"CameraFilterGrayscale vertex shader compile failed: {GL.GetShaderInfoLog(vs)}");
        }

        var fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fragmentSource);
        GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out var fsOk);
        if (fsOk == 0)
        {
            throw new InvalidOperationException($"CameraFilterGrayscale fragment shader compile failed: {GL.GetShaderInfoLog(fs)}");
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linkOk);
        if (linkOk == 0)
        {
            throw new InvalidOperationException($"CameraFilterGrayscale shader link failed: {GL.GetProgramInfoLog(program)}");
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
