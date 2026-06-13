using System.Numerics;
using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed class CymaticSpirals3D : IVisual
{
    private readonly Parameter<float> _baseScale = new("BaseScale", 0.05f, 2.0f, 0.55f);
    private readonly Parameter<float> _lowGain = new("LowGain", 0f, 3f, 1.35f);
    private readonly Parameter<float> _midGain = new("MidGain", 0f, 3f, 1.0f);
    private readonly Parameter<float> _highTwist = new("HighTwist", 0.25f, 8f, 1.8f);
    private readonly Parameter<float> _harmonicF1 = new("HarmonicF1", 0.5f, 4f, 1.5f);
    private readonly Parameter<float> _harmonicF2 = new("HarmonicF2", 0.5f, 4f, 2.33f);
    private readonly Parameter<float> _thetaMax = new("ThetaMax", MathF.PI * 2f, MathF.PI * 20f, MathF.PI * 10f);
    private readonly Parameter<int> _samples = new("SampleCount", 128, 4096, 1400);
    private readonly Parameter<float> _zRotationSpeed = new("ZRotationSpeed", -3f, 3f, 0.35f);
    private readonly Parameter<int> _trailLayers = new("TrailLayers", 1, 32, 10);
    private readonly Parameter<float> _trailTimeStep = new("TrailTimeStep", 0.005f, 0.2f, 0.06f);
    private readonly Parameter<float> _layerDepth = new("LayerDepth", 0f, 0.25f, 0.045f);
    private readonly Parameter<float> _outwardDrift = new("OutwardDrift", -0.2f, 0.6f, 0.08f);
    private readonly Parameter<float> _zigzagAmount = new("HighZigzag", 0f, 1f, 0.16f);
    private readonly Parameter<float> _zigzagFrequency = new("ZigzagFreq", 4f, 120f, 44f);
    private readonly Parameter<float> _lineWidth = new("LineWidth", 1f, 6f, 2f);
    private readonly Parameter<float> _hue = new("Hue", 0f, 360f, 285f);
    private readonly Parameter<float> _brightness = new("Brightness", 0f, 2f, 1.1f);

    private readonly IReadOnlyList<IParameter> _parameters;

    private int _shader;
    private int _vao;
    private int _vbo;
    private int _vertexCapacity;

    private int _uMvp;

    // xyz + rgb
    private float[] _lineVertices = Array.Empty<float>();

    public string Name => "Cymatic Spirals";
    public IReadOnlyList<IParameter> Parameters => _parameters;

    public CymaticSpirals3D()
    {
        _parameters =
        [
            _baseScale,
            _lowGain,
            _midGain,
            _highTwist,
            _harmonicF1,
            _harmonicF2,
            _thetaMax,
            _samples,
            _zRotationSpeed,
            _trailLayers,
            _trailTimeStep,
            _layerDepth,
            _outwardDrift,
            _zigzagAmount,
            _zigzagFrequency,
            _lineWidth,
            _hue,
            _brightness
        ];
    }

    public void Render(float[] spectrum, float time)
    {
        if (_shader == 0)
        {
            CreateGlResources();
        }

        var sampleCount = Math.Clamp(_samples.CurrentValue, 16, 4096);
        EnsureVertexCapacity(sampleCount);

        var (low, mid, high) = AnalyzeBands(spectrum);

        var aLow = (0.25f + (low * 1.75f)) * _lowGain.CurrentValue;
        var aMid = (0.2f + (mid * 1.4f)) * _midGain.CurrentValue;
        var aHigh = _highTwist.CurrentValue * (0.8f + (high * 0.9f));

        var thetaMax = MathF.Max(MathF.PI * 2f, _thetaMax.CurrentValue);
        var f1 = _harmonicF1.CurrentValue;
        var f2 = _harmonicF2.CurrentValue;

        var zigzagAmp = _zigzagAmount.CurrentValue * high * 0.35f;
        var zigzagFreq = _zigzagFrequency.CurrentValue;

        var layers = Math.Clamp(_trailLayers.CurrentValue, 1, 32);
        var layerDepth = _layerDepth.CurrentValue;
        var outwardDrift = _outwardDrift.CurrentValue;
        var trailTimeStep = _trailTimeStep.CurrentValue;

        // Keep depth test off so this source renders reliably into pipeline ping FBOs
        // that may not have a depth attachment.
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.ScissorTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);

        GL.UseProgram(_shader);
        GL.BindVertexArray(_vao);

        var viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);
        var width = Math.Max(1, viewport[2]);
        var height = Math.Max(1, viewport[3]);
        var aspect = width / (float)height;

        var projection = CreateOpenGlPerspective(MathF.PI / 3f, aspect, 0.05f, 100f);
        // Use a simple camera translation (instead of CreateLookAt) to avoid
        // row/column convention pitfalls that can place the whole strip behind the clip volume.
        var view = Matrix4x4.CreateTranslation(0f, 0f, -4.5f);

        GL.LineWidth(_lineWidth.CurrentValue);

        for (var layer = layers - 1; layer >= 0; layer--)
        {
            var ageNorm = layers > 1 ? layer / (float)(layers - 1) : 0f;
            var tLayer = time - (layer * trailTimeStep);

            var scale = _baseScale.CurrentValue * (1f + (ageNorm * outwardDrift));
            var zOffset = -layer * layerDepth;

            var model = Matrix4x4.CreateRotationZ((tLayer * _zRotationSpeed.CurrentValue) + (ageNorm * 0.35f))
                        * Matrix4x4.CreateRotationX(0.35f)
                        * Matrix4x4.CreateTranslation(0f, 0f, zOffset);

            var mvp = model * view * projection;
            var mvpGl = Matrix4x4.Transpose(mvp);
            GL.UniformMatrix4(_uMvp, 1, false, ToArray(mvpGl));

            BuildLayerVertices(sampleCount, thetaMax, aLow, aMid, aHigh, f1, f2, zigzagAmp, zigzagFreq, tLayer, scale, ageNorm, _hue.CurrentValue, _brightness.CurrentValue);

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, sampleCount * 6 * sizeof(float), _lineVertices, BufferUsageHint.DynamicDraw);

            GL.DrawArrays(PrimitiveType.LineStrip, 0, sampleCount);
        }

        GL.LineWidth(1f);
        GL.BindVertexArray(0);
        GL.UseProgram(0);

        GL.Disable(EnableCap.Blend);
    }

    private void BuildLayerVertices(
        int sampleCount,
        float thetaMax,
        float aLow,
        float aMid,
        float aHigh,
        float f1,
        float f2,
        float zigzagAmp,
        float zigzagFreq,
        float layerTime,
        float scale,
        float ageNorm,
        float hue,
        float brightness)
    {
        var idx = 0;
        var inv = sampleCount > 1 ? 1f / (sampleCount - 1) : 1f;

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i * inv;
            var theta = thetaMax * t;

            var r = (aLow * MathF.Sin(f1 * theta)) + (aMid * MathF.Cos(f2 * theta));

            // High-end ripple creates the "micro-zigzag" edge texture.
            var zig = MathF.Sin((theta * zigzagFreq) + (layerTime * 9f)) * zigzagAmp;
            r += zig;

            var angle = theta * aHigh;
            var x = r * MathF.Cos(angle) * scale;
            var y = r * MathF.Sin(angle) * scale;

            _lineVertices[idx++] = x;
            _lineVertices[idx++] = y;
            _lineVertices[idx++] = 0f;

            var hueAtPoint = hue + (t * 120f) - (ageNorm * 40f) + (layerTime * 8f);
            HsvToRgb(hueAtPoint, 0.78f, brightness * (1f - (ageNorm * 0.55f)), out var cr, out var cg, out var cb);

            _lineVertices[idx++] = cr;
            _lineVertices[idx++] = cg;
            _lineVertices[idx++] = cb;
        }
    }

    private void EnsureVertexCapacity(int sampleCount)
    {
        if (sampleCount <= _vertexCapacity)
        {
            return;
        }

        _vertexCapacity = sampleCount;
        _lineVertices = new float[_vertexCapacity * 6];
    }

    private static (float Low, float Mid, float High) AnalyzeBands(float[] spectrum)
    {
        if (spectrum.Length == 0)
        {
            return (0f, 0f, 0f);
        }

        var n = spectrum.Length;
        var lowEnd = Math.Max(1, (int)(n * 0.1f));
        var midEnd = Math.Max(lowEnd + 1, (int)(n * 0.45f));

        var low = AverageNormalizedDb(spectrum, 0, lowEnd);
        var mid = AverageNormalizedDb(spectrum, lowEnd, midEnd);
        var high = AverageNormalizedDb(spectrum, midEnd, n);

        return (low, mid, high);
    }

    private static float AverageNormalizedDb(float[] spectrum, int startInclusive, int endExclusive)
    {
        startInclusive = Math.Clamp(startInclusive, 0, spectrum.Length);
        endExclusive = Math.Clamp(endExclusive, startInclusive + 1, spectrum.Length);

        var sum = 0f;
        var count = 0;

        for (var i = startInclusive; i < endExclusive; i++)
        {
            sum += NormalizeSpectrumDb(spectrum[i]);
            count++;
        }

        return count > 0 ? sum / count : 0f;
    }

    private static float NormalizeSpectrumDb(float db)
    {
        const float minDb = -100f;
        const float maxDb = 0f;
        var normalized = (db - minDb) / (maxDb - minDb);
        return Math.Clamp(normalized, 0f, 1f);
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

    private static Matrix4x4 CreateOpenGlPerspective(float fovY, float aspect, float near, float far)
    {
        var f = 1f / MathF.Tan(fovY * 0.5f);

        return new Matrix4x4(
            f / aspect, 0f, 0f, 0f,
            0f, f, 0f, 0f,
            0f, 0f, (far + near) / (near - far), -1f,
            0f, 0f, (2f * far * near) / (near - far), 0f);
    }

    private static float[] ToArray(Matrix4x4 m) =>
    [
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44
    ];

    private void CreateGlResources()
    {
        const string vertexShaderSource = """
            #version 330 core
            layout (location = 0) in vec3 aPosition;
            layout (location = 1) in vec3 aColor;

            uniform mat4 uMvp;
            out vec3 vColor;

            void main()
            {
                vColor = aColor;
                gl_Position = uMvp * vec4(aPosition, 1.0);
            }
            """;

        const string fragmentShaderSource = """
            #version 330 core
            in vec3 vColor;
            out vec4 fragColor;

            void main()
            {
                fragColor = vec4(vColor, 1.0);
            }
            """;

        _shader = CompileProgram(vertexShaderSource, fragmentShaderSource);
        _uMvp = GL.GetUniformLocation(_shader, "uMvp");

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, 0, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        var stride = 6 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));

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
            throw new InvalidOperationException($"CymaticSpirals3D vertex shader compile failed: {GL.GetShaderInfoLog(vs)}");
        }

        var fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fragmentSource);
        GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out var fsOk);
        if (fsOk == 0)
        {
            throw new InvalidOperationException($"CymaticSpirals3D fragment shader compile failed: {GL.GetShaderInfoLog(fs)}");
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linkOk);
        if (linkOk == 0)
        {
            throw new InvalidOperationException($"CymaticSpirals3D shader link failed: {GL.GetProgramInfoLog(program)}");
        }

        GL.DetachShader(program, vs);
        GL.DetachShader(program, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);

        return program;
    }

    public void Dispose()
    {
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_shader != 0) GL.DeleteProgram(_shader);
    }
}
