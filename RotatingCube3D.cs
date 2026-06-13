using System.Numerics;
using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed class RotatingCube3D : IVisual
{
    private readonly Parameter<float> _rotationSpeedX = new("RotationSpeedX", -6f, 6f, 1.2f);
    private readonly Parameter<float> _rotationSpeedY = new("RotationSpeedY", -6f, 6f, 1.7f);
    private readonly Parameter<float> _colorIntensity = new("ColorIntensity", 0f, 2f, 1f);
    private readonly IReadOnlyList<IParameter> _parameters;

    private int _shader;
    private int _vao;
    private int _vbo;
    private int _ebo;

    private int _uMvp;
    private int _uColorIntensity;

    public string Name => "Rotating Cube 3D";
    public IReadOnlyList<IParameter> Parameters => _parameters;

    public RotatingCube3D()
    {
        _parameters = [_rotationSpeedX, _rotationSpeedY, _colorIntensity];
    }

    public void Render(float[] spectrum, float time)
    {
        if (_shader == 0)
        {
            CreateGlResources();
        }

        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);

        GL.UseProgram(_shader);
        GL.BindVertexArray(_vao);

        var rotX = Matrix4x4.CreateRotationX(time * _rotationSpeedX.CurrentValue);
        var rotY = Matrix4x4.CreateRotationY(time * _rotationSpeedY.CurrentValue);
        var model = rotX * rotY;

        var view = Matrix4x4.CreateLookAt(
            new Vector3(0f, 0.2f, 3.2f),
            Vector3.Zero,
            Vector3.UnitY);

        var viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);
        var width = Math.Max(1, viewport[2]);
        var height = Math.Max(1, viewport[3]);
        var aspect = width / (float)height;

        var projection = CreateOpenGlPerspective(MathF.PI / 3f, aspect, 0.1f, 100f);

        // System.Numerics uses row-vector convention. Build MVP accordingly,
        // then transpose once for GLSL column-vector multiplication.
        var mvp = model * view * projection;
        var mvpGl = Matrix4x4.Transpose(mvp);

        GL.UniformMatrix4(_uMvp, 1, false, ToArray(mvpGl));
        GL.Uniform1(_uColorIntensity, _colorIntensity.CurrentValue);

        GL.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, 0);

        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Disable(EnableCap.DepthTest);
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
            uniform float uColorIntensity;
            out vec4 fragColor;

            void main()
            {
                fragColor = vec4(vColor * uColorIntensity, 1.0);
            }
            """;

        _shader = CompileProgram(vertexShaderSource, fragmentShaderSource);

        _uMvp = GL.GetUniformLocation(_shader, "uMvp");
        _uColorIntensity = GL.GetUniformLocation(_shader, "uColorIntensity");

        var vertices = new float[]
        {
            // position              // color
            -0.6f, -0.6f, -0.6f,     1f, 0f, 0f,
             0.6f, -0.6f, -0.6f,     0f, 1f, 0f,
             0.6f,  0.6f, -0.6f,     0f, 0f, 1f,
            -0.6f,  0.6f, -0.6f,     1f, 1f, 0f,

            -0.6f, -0.6f,  0.6f,     1f, 0f, 1f,
             0.6f, -0.6f,  0.6f,     0f, 1f, 1f,
             0.6f,  0.6f,  0.6f,     1f, 1f, 1f,
            -0.6f,  0.6f,  0.6f,     0.2f, 0.2f, 1f,
        };

        var indices = new uint[]
        {
            // back
            0, 1, 2, 2, 3, 0,
            // front
            4, 5, 6, 6, 7, 4,
            // left
            0, 4, 7, 7, 3, 0,
            // right
            1, 5, 6, 6, 2, 1,
            // bottom
            0, 1, 5, 5, 4, 0,
            // top
            3, 2, 6, 6, 7, 3
        };

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        _ebo = GL.GenBuffer();

        GL.BindVertexArray(_vao);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

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
            throw new InvalidOperationException($"RotatingCube3D vertex shader compile failed: {GL.GetShaderInfoLog(vs)}");
        }

        var fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fragmentSource);
        GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out var fsOk);
        if (fsOk == 0)
        {
            throw new InvalidOperationException($"RotatingCube3D fragment shader compile failed: {GL.GetShaderInfoLog(fs)}");
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linkOk);
        if (linkOk == 0)
        {
            throw new InvalidOperationException($"RotatingCube3D shader link failed: {GL.GetProgramInfoLog(program)}");
        }

        GL.DetachShader(program, vs);
        GL.DetachShader(program, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);

        return program;
    }

    public void Dispose()
    {
        if (_ebo != 0) GL.DeleteBuffer(_ebo);
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_shader != 0) GL.DeleteProgram(_shader);
    }
}
