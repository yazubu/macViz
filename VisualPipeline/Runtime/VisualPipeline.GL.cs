using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private void EnsureRenderTargets()
    {
        var viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);

        var width = Math.Max(1, viewport[2]);
        var height = Math.Max(1, viewport[3]);

        if (width == _renderWidth && height == _renderHeight && _stageTexture != 0)
        {
            foreach (var node in _nodes)
            {
                node.Stage?.OnResize(width, height, this);
            }

            return;
        }

        _renderWidth = width;
        _renderHeight = height;

        if (_stageTexture != 0)
        {
            GL.DeleteTexture(_stageTexture);
            _stageTexture = 0;
        }

        if (_stageFbo != 0)
        {
            GL.DeleteFramebuffer(_stageFbo);
            _stageFbo = 0;
        }

        foreach (var texture in _nodeOutputTextures.Values)
        {
            if (texture != 0)
            {
                GL.DeleteTexture(texture);
            }
        }

        _nodeOutputTextures.Clear();

        _stageTexture = CreateRenderTexture(width, height);
        _stageFbo = GL.GenFramebuffer();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _stageFbo);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _stageTexture,
            0);

        var stageStatus = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (stageStatus != FramebufferErrorCode.FramebufferComplete)
        {
            throw new InvalidOperationException($"VisualPipeline stage framebuffer incomplete: {stageStatus}");
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        foreach (var node in _nodes)
        {
            node.Stage?.OnResize(width, height, this);
        }
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
        const string vertexSource = """
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

        const string blitFragment = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColor;
            uniform sampler2D uTexture;
            void main()
            {
                fragColor = texture(uTexture, vUv);
            }
            """;

        const string blitFlipYFragment = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColor;
            uniform sampler2D uTexture;
            void main()
            {
                fragColor = texture(uTexture, vec2(vUv.x, 1.0 - vUv.y));
            }
            """;

        _blitProgram = CompileProgram(vertexSource, blitFragment);
        _blitProgramFlipY = CompileProgram(vertexSource, blitFlipYFragment);

        GL.UseProgram(_blitProgram);
        GL.Uniform1(GL.GetUniformLocation(_blitProgram, "uTexture"), 0);
        GL.UseProgram(_blitProgramFlipY);
        GL.Uniform1(GL.GetUniformLocation(_blitProgramFlipY, "uTexture"), 0);
        GL.UseProgram(0);

        var vertices = new float[]
        {
            -1f, -1f,   0f, 0f,
             1f, -1f,   1f, 0f,
             1f,  1f,   1f, 1f,
            -1f,  1f,   0f, 1f
        };

        var indices = new uint[] { 0, 1, 2, 2, 3, 0 };

        _quadVao = GL.GenVertexArray();
        _quadVbo = GL.GenBuffer();
        _quadEbo = GL.GenBuffer();

        GL.BindVertexArray(_quadVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _quadVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _quadEbo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);

        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));

        GL.BindVertexArray(0);

        _copyFboRead = GL.GenFramebuffer();
        _copyFboDraw = GL.GenFramebuffer();

        const string blendFragment = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColor;

            uniform sampler2D uBaseTexture;
            uniform sampler2D uLayerTexture;
            uniform float uMix;
            uniform int uBlendMode;

            vec3 blendMode(vec3 baseColor, vec3 layerColor, int mode)
            {
                if (mode == 1) return max(baseColor - layerColor, vec3(0.0));
                if (mode == 2) return baseColor * layerColor;
                if (mode == 3) return min(baseColor, layerColor);
                if (mode == 4) return max(baseColor, layerColor);
                if (mode == 5) return 1.0 - ((1.0 - baseColor) * (1.0 - layerColor));
                if (mode == 6) return abs(baseColor - layerColor);
                if (mode == 7)
                {
                    vec3 low = 2.0 * baseColor * layerColor;
                    vec3 high = 1.0 - (2.0 * (1.0 - baseColor) * (1.0 - layerColor));
                    return mix(low, high, step(vec3(0.5), baseColor));
                }
                if (mode == 8)
                {
                    vec3 low = 2.0 * baseColor * layerColor;
                    vec3 high = 1.0 - (2.0 * (1.0 - baseColor) * (1.0 - layerColor));
                    return mix(low, high, step(vec3(0.5), layerColor));
                }
                if (mode == 9) return clamp(baseColor / max(layerColor, vec3(0.001)), 0.0, 1.0);
                if (mode == 10) return clamp(baseColor / max(vec3(0.001), 1.0 - layerColor), 0.0, 1.0);
                return layerColor;
            }

            void main()
            {
                vec3 baseColor = texture(uBaseTexture, vUv).rgb;
                vec3 layerColor = texture(uLayerTexture, vUv).rgb;
                vec3 blended = blendMode(baseColor, layerColor, clamp(uBlendMode, 0, 10));
                vec3 color = mix(baseColor, blended, clamp(uMix, 0.0, 1.0));
                fragColor = vec4(color, 1.0);
            }
            """;

        _blendProgram = CompileProgram(vertexSource, blendFragment);
        _uBlendBaseTexture = GL.GetUniformLocation(_blendProgram, "uBaseTexture");
        _uBlendLayerTexture = GL.GetUniformLocation(_blendProgram, "uLayerTexture");
        _uBlendMix = GL.GetUniformLocation(_blendProgram, "uMix");
        _uBlendMode = GL.GetUniformLocation(_blendProgram, "uBlendMode");

        GL.UseProgram(_blendProgram);
        GL.Uniform1(_uBlendBaseTexture, 0);
        GL.Uniform1(_uBlendLayerTexture, 1);
        GL.UseProgram(0);
    }

    internal void DrawFullscreen(int shaderProgram, int inputTexture)
    {
        GL.UseProgram(shaderProgram);
        GL.BindVertexArray(_quadVao);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, inputTexture);

        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
    }

    internal void DrawFullscreenWithTextures(int shaderProgram, params (int TextureUnitIndex, int TextureId)[] textures)
    {
        GL.UseProgram(shaderProgram);
        GL.BindVertexArray(_quadVao);

        foreach (var (unit, texture) in textures)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(TextureTarget.Texture2D, texture);
        }

        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
    }

    internal void DrawFullscreenWithTextureBindings(int shaderProgram, params (int TextureUnitIndex, TextureTarget Target, int TextureId)[] textures)
    {
        GL.UseProgram(shaderProgram);
        GL.BindVertexArray(_quadVao);

        foreach (var (unit, target, texture) in textures)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(target, texture);
        }

        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
    }

    internal void CopyTexture(int sourceTexture, int targetTexture)
    {
        if (sourceTexture == 0 || targetTexture == 0 || _renderWidth <= 0 || _renderHeight <= 0)
        {
            return;
        }

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _copyFboRead);
        GL.FramebufferTexture2D(
            FramebufferTarget.ReadFramebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            sourceTexture,
            0);

        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _copyFboDraw);
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

    internal bool TryReadTextureRgba(int sourceTexture, byte[] destination)
    {
        if (sourceTexture == 0 || _renderWidth <= 0 || _renderHeight <= 0)
        {
            return false;
        }

        var requiredBytes = _renderWidth * _renderHeight * 4;
        if (destination.Length < requiredBytes)
        {
            return false;
        }

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _copyFboRead);
        GL.FramebufferTexture2D(
            FramebufferTarget.ReadFramebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            sourceTexture,
            0);

        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadPixels(0, 0, _renderWidth, _renderHeight, PixelFormat.Rgba, PixelType.UnsignedByte, destination);

        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        return true;
    }

    private static int CompileProgram(string vertexSource, string fragmentSource)
    {
        var vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, vertexSource);
        GL.CompileShader(vs);
        GL.GetShader(vs, ShaderParameter.CompileStatus, out var vsOk);
        if (vsOk == 0)
        {
            throw new InvalidOperationException($"VisualPipeline vertex shader compile failed: {GL.GetShaderInfoLog(vs)}");
        }

        var fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fragmentSource);
        GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out var fsOk);
        if (fsOk == 0)
        {
            throw new InvalidOperationException($"VisualPipeline fragment shader compile failed: {GL.GetShaderInfoLog(fs)}");
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linkOk);
        if (linkOk == 0)
        {
            throw new InvalidOperationException($"VisualPipeline shader link failed: {GL.GetProgramInfoLog(program)}");
        }

        GL.DetachShader(program, vs);
        GL.DetachShader(program, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);

        return program;
    }
}
