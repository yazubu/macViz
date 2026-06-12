using System.Numerics;
using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed class RotatingParticleSystem3D : IVisual
{
    private const int MaxParticles = 4096;

    private readonly Parameter<int> _particleCount = new("ParticleCount", 64, MaxParticles, 1200);
    private readonly Parameter<float> _rotationSpeedX = new("RotationSpeedX", -4f, 4f, 0.7f);
    private readonly Parameter<float> _rotationSpeedY = new("RotationSpeedY", -4f, 4f, 1.1f);
    private readonly Parameter<float> _radius = new("Radius", 0.2f, 3.0f, 1.1f);
    private readonly Parameter<float> _pointSize = new("PointSize", 1f, 16f, 4f);
    private readonly Parameter<float> _colorIntensity = new("ColorIntensity", 0f, 2f, 1f);
    private readonly IReadOnlyList<IParameter> _parameters;

    private int _shader;
    private int _vao;
    private int _vbo;

    private int _uMvp;
    private int _uPointSize;
    private int _uColorIntensity;
    private int _uRadius;
    private int _uTime;

    public string Name => "Rotating Particles 3D";
    public IReadOnlyList<IParameter> Parameters => _parameters;

    public RotatingParticleSystem3D()
    {
        _parameters = [_particleCount, _rotationSpeedX, _rotationSpeedY, _radius, _pointSize, _colorIntensity];
        CreateGlResources();
    }

    public void Render(float[] spectrum, float time)
    {
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);

        GL.UseProgram(_shader);
        GL.BindVertexArray(_vao);

        var rotX = Matrix4x4.CreateRotationX(time * _rotationSpeedX.CurrentValue);
        var rotY = Matrix4x4.CreateRotationY(time * _rotationSpeedY.CurrentValue);
        var model = rotX * rotY;

        var view = Matrix4x4.CreateLookAt(
            new Vector3(0f, 0f, 4.5f),
            Vector3.Zero,
            Vector3.UnitY);

        var viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);
        var width = Math.Max(1, viewport[2]);
        var height = Math.Max(1, viewport[3]);
        var aspect = width / (float)height;

        var projection = CreateOpenGlPerspective(MathF.PI / 3f, aspect, 0.1f, 100f);

        var mvp = model * view * projection;
        var mvpGl = Matrix4x4.Transpose(mvp);

        GL.UniformMatrix4(_uMvp, 1, false, ToArray(mvpGl));
        GL.Uniform1(_uPointSize, _pointSize.CurrentValue);
        GL.Uniform1(_uColorIntensity, _colorIntensity.CurrentValue);
        GL.Uniform1(_uRadius, _radius.CurrentValue);
        GL.Uniform1(_uTime, time);

        var count = Math.Clamp(_particleCount.CurrentValue, 1, MaxParticles);
        GL.DrawArrays(PrimitiveType.Points, 0, count);

        GL.BindVertexArray(0);
        GL.UseProgram(0);

        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.DepthTest);
    }

    private void CreateGlResources()
    {
        const string vertexShaderSource = """
            #version 330 core
            layout (location = 0) in vec3 aPosition;

            uniform mat4 uMvp;
            uniform float uPointSize;
            uniform float uRadius;
            uniform float uTime;

            out vec3 vColor;

            void main()
            {
                float id = float(gl_VertexID);
                vec3 n = normalize(aPosition);

                // Build a local tangent frame so each particle can move on/around the sphere.
                vec3 up = abs(n.y) > 0.98 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
                vec3 t = normalize(cross(up, n));
                vec3 b = normalize(cross(n, t));

                // Per-particle phase/speed so points move relative to each other.
                float phaseA = id * 0.173 + aPosition.x * 3.0;
                float phaseB = id * 0.097 + aPosition.z * 5.0;
                float speedA = 0.8 + fract(id * 0.013) * 2.2;
                float speedB = 0.6 + fract(id * 0.021) * 1.7;

                float swirlA = sin(uTime * speedA + phaseA);
                float swirlB = cos(uTime * speedB + phaseB);
                float radial = sin(uTime * (0.5 + fract(id * 0.007) * 1.4) + phaseB) * 0.18;

                float baseRadius = uRadius * (1.0 + radial);
                vec3 p = n * baseRadius;
                p += t * swirlA * 0.12;
                p += b * swirlB * 0.12;

                gl_Position = uMvp * vec4(p, 1.0);
                gl_PointSize = uPointSize * (0.85 + 0.3 * (swirlA * 0.5 + 0.5));

                float twinkle = 0.6 + 0.4 * sin(uTime * 2.5 + phaseA);
                vColor = vec3(
                    0.2 + 0.8 * abs(aPosition.x),
                    0.3 + 0.7 * abs(aPosition.y),
                    0.6 + 0.4 * abs(aPosition.z)) * twinkle;
            }
            """;

        const string fragmentShaderSource = """
            #version 330 core
            in vec3 vColor;
            uniform float uColorIntensity;
            out vec4 fragColor;

            void main()
            {
                vec2 uv = gl_PointCoord * 2.0 - 1.0;
                float r2 = dot(uv, uv);
                if (r2 > 1.0) discard;

                float alpha = 1.0 - smoothstep(0.0, 1.0, r2);
                vec3 c = vColor * uColorIntensity;
                fragColor = vec4(c, alpha);
            }
            """;

        _shader = CompileProgram(vertexShaderSource, fragmentShaderSource);

        _uMvp = GL.GetUniformLocation(_shader, "uMvp");
        _uPointSize = GL.GetUniformLocation(_shader, "uPointSize");
        _uColorIntensity = GL.GetUniformLocation(_shader, "uColorIntensity");
        _uRadius = GL.GetUniformLocation(_shader, "uRadius");
        _uTime = GL.GetUniformLocation(_shader, "uTime");

        var vertices = BuildSpherePoints(MaxParticles);

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    private static float[] BuildSpherePoints(int count)
    {
        var data = new float[count * 3];
        var golden = MathF.PI * (3f - MathF.Sqrt(5f));

        for (var i = 0; i < count; i++)
        {
            var t = i / (float)(count - 1);
            var y = 1f - (2f * t);
            var radius = MathF.Sqrt(MathF.Max(0f, 1f - (y * y)));
            var theta = golden * i;

            var x = MathF.Cos(theta) * radius;
            var z = MathF.Sin(theta) * radius;

            var idx = i * 3;
            data[idx] = x;
            data[idx + 1] = y;
            data[idx + 2] = z;
        }

        return data;
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

    private static int CompileProgram(string vertexSource, string fragmentSource)
    {
        var vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, vertexSource);
        GL.CompileShader(vs);
        GL.GetShader(vs, ShaderParameter.CompileStatus, out var vsOk);
        if (vsOk == 0)
        {
            throw new InvalidOperationException($"RotatingParticleSystem3D vertex shader compile failed: {GL.GetShaderInfoLog(vs)}");
        }

        var fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fragmentSource);
        GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out var fsOk);
        if (fsOk == 0)
        {
            throw new InvalidOperationException($"RotatingParticleSystem3D fragment shader compile failed: {GL.GetShaderInfoLog(fs)}");
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linkOk);
        if (linkOk == 0)
        {
            throw new InvalidOperationException($"RotatingParticleSystem3D shader link failed: {GL.GetProgramInfoLog(program)}");
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
