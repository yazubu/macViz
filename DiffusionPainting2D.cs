using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed class DiffusionPainting2D : IVisual
{
    private readonly Parameter<float> _feedRate = new("FeedRate", 0.001f, 0.12f, 0.0367f);
    private readonly Parameter<float> _killRate = new("KillRate", 0.02f, 0.09f, 0.0649f);
    private readonly Parameter<float> _diffuseU = new("DiffuseU", 0.05f, 0.35f, 0.2097f);
    private readonly Parameter<float> _diffuseV = new("DiffuseV", 0.02f, 0.22f, 0.105f);
    private readonly Parameter<float> _timeStep = new("TimeStep", 0.2f, 2.0f, 1.0f);
    private readonly Parameter<int> _iterations = new("Iterations", 1, 32, 10);
    private readonly Parameter<float> _resolutionScale = new("ResolutionScale", 0.25f, 1.0f, 0.65f);

    // Palette controls (mod-matrix friendly)
    private readonly Parameter<float> _paletteHue = new("PaletteHue", 0f, 360f, 210f);
    private readonly Parameter<float> _paletteSaturation = new("PaletteSaturation", 0f, 2f, 1.15f);
    private readonly Parameter<float> _paletteContrast = new("PaletteContrast", 0.3f, 3f, 1.25f);
    private readonly Parameter<float> _paletteBrightness = new("PaletteBrightness", 0f, 2f, 1.0f);
    private readonly Parameter<float> _paletteCycle = new("PaletteCycle", 0f, 3f, 0.35f);

    private readonly IReadOnlyList<IParameter> _parameters;

    private int _simProgram;
    private int _displayProgram;
    private int _vao;
    private int _vbo;

    private readonly int[] _simTextures = new int[2];
    private readonly int[] _simFbos = new int[2];
    private int _readIndex;

    private int _simWidth;
    private int _simHeight;

    private int _uSimTexture;
    private int _uTexelSize;
    private int _uFeed;
    private int _uKill;
    private int _uDiffU;
    private int _uDiffV;
    private int _uDt;
    private int _uTime;
    private int _uAudioDrive;

    private int _uDisplayTexture;
    private int _uHue;
    private int _uSaturation;
    private int _uContrast;
    private int _uBrightness;
    private int _uCycle;
    private int _uDisplayTime;

    public string Name => "Diffusion Painting";
    public IReadOnlyList<IParameter> Parameters => _parameters;

    public DiffusionPainting2D()
    {
        _parameters =
        [
            _feedRate,
            _killRate,
            _diffuseU,
            _diffuseV,
            _timeStep,
            _iterations,
            _resolutionScale,
            _paletteHue,
            _paletteSaturation,
            _paletteContrast,
            _paletteBrightness,
            _paletteCycle
        ];
    }

    public void Render(float[] spectrum, float time)
    {
        EnsureResources();

        var viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);
        var width = Math.Max(1, viewport[2]);
        var height = Math.Max(1, viewport[3]);

        var scale = Math.Clamp(_resolutionScale.CurrentValue, 0.25f, 1f);
        var targetW = Math.Max(16, (int)(width * scale));
        var targetH = Math.Max(16, (int)(height * scale));

        EnsureSimulationTargets(targetW, targetH);

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.ScissorTest);

        var audioDrive = AnalyzeAudioDrive(spectrum);

        var iterations = Math.Clamp(_iterations.CurrentValue, 1, 32);
        for (var i = 0; i < iterations; i++)
        {
            var writeIndex = 1 - _readIndex;

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _simFbos[writeIndex]);
            GL.Viewport(0, 0, _simWidth, _simHeight);

            GL.UseProgram(_simProgram);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _simTextures[_readIndex]);

            GL.Uniform2(_uTexelSize, 1f / _simWidth, 1f / _simHeight);
            GL.Uniform1(_uFeed, _feedRate.CurrentValue);
            GL.Uniform1(_uKill, _killRate.CurrentValue);
            GL.Uniform1(_uDiffU, _diffuseU.CurrentValue);
            GL.Uniform1(_uDiffV, _diffuseV.CurrentValue);
            GL.Uniform1(_uDt, _timeStep.CurrentValue);
            GL.Uniform1(_uTime, time + (i * 0.01f));
            GL.Uniform1(_uAudioDrive, audioDrive);

            DrawFullscreenQuad();

            _readIndex = writeIndex;
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, width, height);

        GL.UseProgram(_displayProgram);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _simTextures[_readIndex]);

        GL.Uniform1(_uHue, _paletteHue.CurrentValue);
        GL.Uniform1(_uSaturation, _paletteSaturation.CurrentValue);
        GL.Uniform1(_uContrast, _paletteContrast.CurrentValue);
        GL.Uniform1(_uBrightness, _paletteBrightness.CurrentValue);
        GL.Uniform1(_uCycle, _paletteCycle.CurrentValue);
        GL.Uniform1(_uDisplayTime, time);

        DrawFullscreenQuad();

        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    private void EnsureResources()
    {
        if (_simProgram != 0 && _displayProgram != 0 && _vao != 0)
        {
            return;
        }

        CreateQuadResources();
        CreatePrograms();
    }

    private void EnsureSimulationTargets(int width, int height)
    {
        if (width == _simWidth && height == _simHeight && _simTextures[0] != 0 && _simTextures[1] != 0)
        {
            return;
        }

        for (var i = 0; i < 2; i++)
        {
            if (_simFbos[i] != 0)
            {
                GL.DeleteFramebuffer(_simFbos[i]);
                _simFbos[i] = 0;
            }

            if (_simTextures[i] != 0)
            {
                GL.DeleteTexture(_simTextures[i]);
                _simTextures[i] = 0;
            }
        }

        _simWidth = width;
        _simHeight = height;

        var initial = BuildInitialState(width, height);

        for (var i = 0; i < 2; i++)
        {
            _simTextures[i] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _simTextures[i]);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rg32f,
                width,
                height,
                0,
                PixelFormat.Rg,
                PixelType.Float,
                initial);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            _simFbos[i] = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _simFbos[i]);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _simTextures[i],
                0);

            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                throw new InvalidOperationException($"DiffusionPainting FBO incomplete: {status}");
            }
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        _readIndex = 0;
    }

    private static float[] BuildInitialState(int width, int height)
    {
        var data = new float[width * height * 2];

        for (var i = 0; i < width * height; i++)
        {
            data[(i * 2)] = 1f;       // U
            data[(i * 2) + 1] = 0f;   // V
        }

        var random = new Random(1337);

        // Seed the simulation with random V patches.
        for (var s = 0; s < 48; s++)
        {
            var cx = random.Next(0, width);
            var cy = random.Next(0, height);
            var radius = random.Next(4, Math.Max(8, Math.Min(width, height) / 16));

            var r2 = radius * radius;
            var x0 = Math.Max(0, cx - radius);
            var x1 = Math.Min(width - 1, cx + radius);
            var y0 = Math.Max(0, cy - radius);
            var y1 = Math.Min(height - 1, cy + radius);

            for (var y = y0; y <= y1; y++)
            {
                var dy = y - cy;
                for (var x = x0; x <= x1; x++)
                {
                    var dx = x - cx;
                    if ((dx * dx) + (dy * dy) > r2)
                    {
                        continue;
                    }

                    var idx = ((y * width) + x) * 2;
                    data[idx] = 0.1f;
                    data[idx + 1] = 0.95f;
                }
            }
        }

        return data;
    }

    private static float AnalyzeAudioDrive(float[] spectrum)
    {
        if (spectrum.Length == 0)
        {
            return 0f;
        }

        var lowEnd = Math.Max(1, (int)(spectrum.Length * 0.18f));
        var sum = 0f;
        for (var i = 0; i < lowEnd; i++)
        {
            var normalized = (spectrum[i] + 100f) / 100f;
            sum += Math.Clamp(normalized, 0f, 1f);
        }

        var avg = sum / lowEnd;
        return avg * avg;
    }

    private void CreateQuadResources()
    {
        var vertices = new float[]
        {
            // position   // uv
            -1f, -1f,     0f, 0f,
             1f, -1f,     1f, 0f,
             1f,  1f,     1f, 1f,
            -1f, -1f,     0f, 0f,
             1f,  1f,     1f, 1f,
            -1f,  1f,     0f, 1f
        };

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));

        GL.BindVertexArray(0);
    }

    private void DrawFullscreenQuad()
    {
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    private void CreatePrograms()
    {
        const string vertex = """
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

        const string simFragment = """
            #version 330 core
            in vec2 vUv;
            out vec2 fragUV;

            uniform sampler2D uState;
            uniform vec2 uTexelSize;
            uniform float uFeed;
            uniform float uKill;
            uniform float uDiffU;
            uniform float uDiffV;
            uniform float uDt;
            uniform float uTime;
            uniform float uAudioDrive;

            vec2 sampleState(vec2 uv)
            {
                return texture(uState, uv).rg;
            }

            void main()
            {
                vec2 c = sampleState(vUv);
                float U = c.r;
                float V = c.g;

                vec2 n  = sampleState(vUv + vec2(0.0,  uTexelSize.y));
                vec2 s  = sampleState(vUv - vec2(0.0,  uTexelSize.y));
                vec2 e  = sampleState(vUv + vec2(uTexelSize.x, 0.0));
                vec2 w  = sampleState(vUv - vec2(uTexelSize.x, 0.0));
                vec2 ne = sampleState(vUv + vec2(uTexelSize.x,  uTexelSize.y));
                vec2 nw = sampleState(vUv + vec2(-uTexelSize.x, uTexelSize.y));
                vec2 se = sampleState(vUv + vec2(uTexelSize.x, -uTexelSize.y));
                vec2 sw = sampleState(vUv + vec2(-uTexelSize.x,-uTexelSize.y));

                float lapU = (n.r + s.r + e.r + w.r) * 0.2 + (ne.r + nw.r + se.r + sw.r) * 0.05 - U;
                float lapV = (n.g + s.g + e.g + w.g) * 0.2 + (ne.g + nw.g + se.g + sw.g) * 0.05 - V;

                float uvv = U * V * V;

                float dU = uDiffU * lapU - uvv + uFeed * (1.0 - U);
                float dV = uDiffV * lapV + uvv - (uKill + uFeed) * V;

                U += dU * uDt;
                V += dV * uDt;

                // Audio-reactive injection pulse.
                vec2 p = vec2(0.5 + 0.22 * sin(uTime * 0.37), 0.5 + 0.22 * cos(uTime * 0.41));
                float d = distance(vUv, p);
                float pulse = smoothstep(0.065, 0.0, d) * uAudioDrive;
                V += pulse * 0.05;
                U -= pulse * 0.03;

                fragUV = clamp(vec2(U, V), 0.0, 1.0);
            }
            """;

        const string displayFragment = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColor;

            uniform sampler2D uState;
            uniform float uHue;
            uniform float uSaturation;
            uniform float uContrast;
            uniform float uBrightness;
            uniform float uCycle;
            uniform float uTime;

            vec3 hsv2rgb(vec3 c)
            {
                vec4 K = vec4(1.0, 2.0/3.0, 1.0/3.0, 3.0);
                vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
            }

            void main()
            {
                vec2 uv = texture(uState, vUv).rg;
                float U = uv.r;
                float V = uv.g;

                float t = clamp((V - U) * 0.5 + 0.5, 0.0, 1.0);
                t = pow(t, max(0.01, uContrast));

                float hue = fract((uHue / 360.0) + t * 0.35 + uTime * 0.03 * uCycle);
                float sat = clamp(uSaturation, 0.0, 2.0);
                float val = clamp((0.25 + 0.85 * t) * uBrightness, 0.0, 2.0);

                vec3 col = hsv2rgb(vec3(hue, min(1.0, sat), min(1.0, val)));

                // Add a soft second tone for a richer psychedelic palette.
                float hue2 = fract(hue + 0.33 + 0.1 * sin(uTime * (0.4 + uCycle)));
                vec3 col2 = hsv2rgb(vec3(hue2, min(1.0, sat * 0.8), min(1.0, val * 0.8)));
                col = mix(col, col2, smoothstep(0.2, 0.9, t));

                fragColor = vec4(col, 1.0);
            }
            """;

        _simProgram = CompileProgram(vertex, simFragment);
        _displayProgram = CompileProgram(vertex, displayFragment);

        _uSimTexture = GL.GetUniformLocation(_simProgram, "uState");
        _uTexelSize = GL.GetUniformLocation(_simProgram, "uTexelSize");
        _uFeed = GL.GetUniformLocation(_simProgram, "uFeed");
        _uKill = GL.GetUniformLocation(_simProgram, "uKill");
        _uDiffU = GL.GetUniformLocation(_simProgram, "uDiffU");
        _uDiffV = GL.GetUniformLocation(_simProgram, "uDiffV");
        _uDt = GL.GetUniformLocation(_simProgram, "uDt");
        _uTime = GL.GetUniformLocation(_simProgram, "uTime");
        _uAudioDrive = GL.GetUniformLocation(_simProgram, "uAudioDrive");

        _uDisplayTexture = GL.GetUniformLocation(_displayProgram, "uState");
        _uHue = GL.GetUniformLocation(_displayProgram, "uHue");
        _uSaturation = GL.GetUniformLocation(_displayProgram, "uSaturation");
        _uContrast = GL.GetUniformLocation(_displayProgram, "uContrast");
        _uBrightness = GL.GetUniformLocation(_displayProgram, "uBrightness");
        _uCycle = GL.GetUniformLocation(_displayProgram, "uCycle");
        _uDisplayTime = GL.GetUniformLocation(_displayProgram, "uTime");

        GL.UseProgram(_simProgram);
        GL.Uniform1(_uSimTexture, 0);
        GL.UseProgram(_displayProgram);
        GL.Uniform1(_uDisplayTexture, 0);
        GL.UseProgram(0);
    }

    private static int CompileProgram(string vertexSource, string fragmentSource)
    {
        var vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, vertexSource);
        GL.CompileShader(vs);
        GL.GetShader(vs, ShaderParameter.CompileStatus, out var vsOk);
        if (vsOk == 0)
        {
            throw new InvalidOperationException($"DiffusionPainting2D vertex shader compile failed: {GL.GetShaderInfoLog(vs)}");
        }

        var fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fragmentSource);
        GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out var fsOk);
        if (fsOk == 0)
        {
            throw new InvalidOperationException($"DiffusionPainting2D fragment shader compile failed: {GL.GetShaderInfoLog(fs)}");
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linkOk);
        if (linkOk == 0)
        {
            throw new InvalidOperationException($"DiffusionPainting2D shader link failed: {GL.GetProgramInfoLog(program)}");
        }

        GL.DetachShader(program, vs);
        GL.DetachShader(program, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);

        return program;
    }

    public void Dispose()
    {
        for (var i = 0; i < 2; i++)
        {
            if (_simFbos[i] != 0)
            {
                GL.DeleteFramebuffer(_simFbos[i]);
                _simFbos[i] = 0;
            }

            if (_simTextures[i] != 0)
            {
                GL.DeleteTexture(_simTextures[i]);
                _simTextures[i] = 0;
            }
        }

        if (_vbo != 0)
        {
            GL.DeleteBuffer(_vbo);
            _vbo = 0;
        }

        if (_vao != 0)
        {
            GL.DeleteVertexArray(_vao);
            _vao = 0;
        }

        if (_simProgram != 0)
        {
            GL.DeleteProgram(_simProgram);
            _simProgram = 0;
        }

        if (_displayProgram != 0)
        {
            GL.DeleteProgram(_displayProgram);
            _displayProgram = 0;
        }
    }
}
