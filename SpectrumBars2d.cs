using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed class SpectrumBars2d : IVisual
{
    private float[] _vertices = Array.Empty<float>();

    private int _shader;
    private int _vao;
    private int _vbo;
    private int _colorLocation;

    private readonly Parameter<int> _barCount = new("BarCount", 8, 128, 64);
    private readonly Parameter<float> _colorHue = new("ColorHue", 0f, 360f, 190f);
    private readonly Parameter<float> _scaleY = new("ScaleY", 0.1f, 5.0f, 1.0f);

    private readonly IReadOnlyList<IParameter> _parameters;

    public string Name => "Spectrum Bars 2D";
    public IReadOnlyList<IParameter> Parameters => _parameters;

    public SpectrumBars2d()
    {
        _parameters = [_barCount, _colorHue, _scaleY];
    }

    public void Render(float[] spectrum, float time)
    {
        if (_shader == 0)
        {
            CreateGlResources();
        }

        if (spectrum.Length == 0)
        {
            return;
        }

        var bars = _barCount.CurrentValue;
        EnsureVertexCapacity(bars);
        BuildVertices(spectrum, bars, _scaleY.CurrentValue);

        HsvToRgb(_colorHue.CurrentValue, 0.8f, 1.0f, out var r, out var g, out var b);

        GL.UseProgram(_shader);
        GL.Uniform3(_colorLocation, r, g, b);
        GL.BindVertexArray(_vao);

        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.ScissorTest);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertices.Length * sizeof(float), _vertices, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertices.Length / 2);

        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    private void EnsureVertexCapacity(int barCount)
    {
        var required = barCount * 6 * 2;
        if (_vertices.Length != required)
        {
            _vertices = new float[required];
        }
    }

    private void BuildVertices(float[] spectrum, int barCount, float scaleY)
    {
        var binsPerBar = Math.Max(1, spectrum.Length / barCount);
        var step = 2f / barCount;
        var barWidth = step * 0.82f;
        var sidePadding = (step - barWidth) * 0.5f;

        var v = 0;
        for (var i = 0; i < barCount; i++)
        {
            var start = i * binsPerBar;
            var end = Math.Min(spectrum.Length, start + binsPerBar);

            var sum = 0f;
            for (var b = start; b < end; b++)
            {
                sum += spectrum[b];
            }

            var db = sum / Math.Max(1, end - start);
            var normalized = Math.Clamp((db + 80f) / 80f, 0f, 1f);
            normalized = MathF.Max(normalized, 0.01f);

            var x0 = -1f + (i * step) + sidePadding;
            var x1 = x0 + barWidth;
            var y0 = -1f;
            var y1 = -1f + (normalized * 2.0f * scaleY);
            y1 = Math.Clamp(y1, -1f, 1f);

            _vertices[v++] = x0; _vertices[v++] = y0;
            _vertices[v++] = x1; _vertices[v++] = y0;
            _vertices[v++] = x1; _vertices[v++] = y1;

            _vertices[v++] = x0; _vertices[v++] = y0;
            _vertices[v++] = x1; _vertices[v++] = y1;
            _vertices[v++] = x0; _vertices[v++] = y1;
        }
    }

    private static void HsvToRgb(float hueDeg, float sat, float val, out float r, out float g, out float b)
    {
        var hue = (hueDeg % 360f + 360f) % 360f;
        var c = val * sat;
        var x = c * (1 - MathF.Abs((hue / 60f) % 2 - 1));
        var m = val - c;

        if (hue < 60f) { r = c; g = x; b = 0f; }
        else if (hue < 120f) { r = x; g = c; b = 0f; }
        else if (hue < 180f) { r = 0f; g = c; b = x; }
        else if (hue < 240f) { r = 0f; g = x; b = c; }
        else if (hue < 300f) { r = x; g = 0f; b = c; }
        else { r = c; g = 0f; b = x; }

        r += m;
        g += m;
        b += m;
    }

    private void CreateGlResources()
    {
        const string vertexSource = """
            #version 330 core
            layout (location = 0) in vec2 aPosition;

            void main()
            {
                gl_Position = vec4(aPosition, 0.0, 1.0);
            }
            """;

        const string fragmentSource = """
            #version 330 core
            uniform vec3 uColor;
            out vec4 fragColor;

            void main()
            {
                fragColor = vec4(uColor, 1.0);
            }
            """;

        var vertexShader = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertexShader, vertexSource);
        GL.CompileShader(vertexShader);
        GL.GetShader(vertexShader, ShaderParameter.CompileStatus, out var vertexStatus);
        if (vertexStatus == 0)
        {
            throw new InvalidOperationException($"SpectrumBars2d vertex shader compile failed: {GL.GetShaderInfoLog(vertexShader)}");
        }

        var fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragmentShader, fragmentSource);
        GL.CompileShader(fragmentShader);
        GL.GetShader(fragmentShader, ShaderParameter.CompileStatus, out var fragmentStatus);
        if (fragmentStatus == 0)
        {
            throw new InvalidOperationException($"SpectrumBars2d fragment shader compile failed: {GL.GetShaderInfoLog(fragmentShader)}");
        }

        _shader = GL.CreateProgram();
        GL.AttachShader(_shader, vertexShader);
        GL.AttachShader(_shader, fragmentShader);
        GL.LinkProgram(_shader);
        GL.GetProgram(_shader, GetProgramParameterName.LinkStatus, out var linkStatus);
        if (linkStatus == 0)
        {
            throw new InvalidOperationException($"SpectrumBars2d shader link failed: {GL.GetProgramInfoLog(_shader)}");
        }

        GL.DetachShader(_shader, vertexShader);
        GL.DetachShader(_shader, fragmentShader);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);

        _colorLocation = GL.GetUniformLocation(_shader, "uColor");

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, 0, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), IntPtr.Zero);

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    public void Dispose()
    {
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_shader != 0) GL.DeleteProgram(_shader);
    }
}
