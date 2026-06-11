using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed class SpectrumBars2d : IVisual
{
    private const int BarCount = 64;
    private readonly float[] _vertices = new float[BarCount * 6 * 2];

    private int _shader;
    private int _vao;
    private int _vbo;

    public string Name => "Spectrum Bars 2D";

    public SpectrumBars2d()
    {
        CreateGlResources();
    }

    public void Render(float[] spectrum, float time)
    {
        if (spectrum.Length == 0)
        {
            return;
        }

        BuildVertices(spectrum);

        GL.UseProgram(_shader);
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

    private void BuildVertices(float[] spectrum)
    {
        var binsPerBar = Math.Max(1, spectrum.Length / BarCount);
        var step = 2f / BarCount;
        var barWidth = step * 0.82f;
        var sidePadding = (step - barWidth) * 0.5f;

        var v = 0;
        for (var i = 0; i < BarCount; i++)
        {
            var start = i * binsPerBar;
            var end = Math.Min(spectrum.Length, start + binsPerBar);

            var sum = 0f;
            for (var b = start; b < end; b++)
            {
                sum += spectrum[b];
            }

            var db = sum / Math.Max(1, end - start);
            var normalized = Math.Clamp((db + 120f) / 126f, 0f, 1f);
            normalized = MathF.Max(normalized, 0.03f);

            var x0 = -1f + (i * step) + sidePadding;
            var x1 = x0 + barWidth;
            var y0 = -1f;
            var y1 = -1f + (normalized * 1.9f);

            _vertices[v++] = x0; _vertices[v++] = y0;
            _vertices[v++] = x1; _vertices[v++] = y0;
            _vertices[v++] = x1; _vertices[v++] = y1;

            _vertices[v++] = x0; _vertices[v++] = y0;
            _vertices[v++] = x1; _vertices[v++] = y1;
            _vertices[v++] = x0; _vertices[v++] = y1;
        }
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
            out vec4 fragColor;

            void main()
            {
                fragColor = vec4(0.2, 0.9, 1.0, 1.0);
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

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertices.Length * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
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
