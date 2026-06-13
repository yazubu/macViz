using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class CodecBleedEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.codecBleed";

        private readonly Parameter<float> _intensity = new("Codec Bleed / Intensity", 0f, 8f, 1.35f);
        private readonly Parameter<float> _flowGain = new("Codec Bleed / Flow Gain", 0f, 4f, 1f);
        private readonly Parameter<float> _freshMix = new("Codec Bleed / Fresh Mix", 0f, 0.2f, 0.01f);
        private readonly Parameter<float> _feedbackDecay = new("Codec Bleed / Feedback Decay", 0.85f, 1f, 0.995f);
        private readonly Parameter<float> _mix = new("Codec Bleed / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uCurrentTexture;
        private int _uPrevInputTexture;
        private int _uPrevFeedback;
        private int _uIntensity;
        private int _uFlowGain;
        private int _uFreshMix;
        private int _uFeedbackDecay;
        private int _uMix;

        private readonly int[] _feedbackTextures = new int[2];
        private readonly int[] _feedbackFbos = new int[2];
        private int _feedbackWriteIndex;
        private bool _hasFeedbackHistory;

        private int _prevInputTexture;
        private bool _hasPrevInput;

        private int _lastWidth;
        private int _lastHeight;

        public CodecBleedEffectStage()
        {
            _parameters = [_intensity, _flowGain, _freshMix, _feedbackDecay, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Codec Bleed";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

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

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;

                uniform sampler2D uCurrentTexture;
                uniform sampler2D uPrevInputTexture;
                uniform sampler2D uPrevFeedback;
                uniform float uIntensity;
                uniform float uFlowGain;
                uniform float uFreshMix;
                uniform float uFeedbackDecay;
                uniform float uMix;

                float luma(vec3 c)
                {
                    return dot(c, vec3(0.299, 0.587, 0.114));
                }

                float lumaDelta(vec2 uv)
                {
                    float curr = luma(texture(uCurrentTexture, clamp(uv, vec2(0.0), vec2(1.0))).rgb);
                    float prev = luma(texture(uPrevInputTexture, clamp(uv, vec2(0.0), vec2(1.0))).rgb);
                    return curr - prev;
                }

                void main()
                {
                    vec3 currentColor = texture(uCurrentTexture, vUv).rgb;

                    vec2 texSize = vec2(textureSize(uCurrentTexture, 0));
                    vec2 safeTexSize = max(texSize, vec2(1.0));
                    vec2 texel = 1.0 / safeTexSize;

                    float dx = lumaDelta(vUv + vec2(texel.x, 0.0)) - lumaDelta(vUv - vec2(texel.x, 0.0));
                    float dy = lumaDelta(vUv + vec2(0.0, texel.y)) - lumaDelta(vUv - vec2(0.0, texel.y));

                    vec2 motionPixels = vec2(dx, dy) * max(uFlowGain, 0.0) * 48.0;
                    vec2 uvOffset = (motionPixels / safeTexSize) * uIntensity;
                    vec2 bleedUv = clamp(vUv + uvOffset, vec2(0.0), vec2(1.0));

                    vec3 feedbackColor = texture(uPrevFeedback, bleedUv).rgb * clamp(uFeedbackDecay, 0.0, 1.0);
                    vec3 bled = mix(feedbackColor, currentColor, clamp(uFreshMix, 0.0, 1.0));
                    vec3 color = mix(currentColor, bled, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uCurrentTexture = GL.GetUniformLocation(_program, "uCurrentTexture");
            _uPrevInputTexture = GL.GetUniformLocation(_program, "uPrevInputTexture");
            _uPrevFeedback = GL.GetUniformLocation(_program, "uPrevFeedback");
            _uIntensity = GL.GetUniformLocation(_program, "uIntensity");
            _uFlowGain = GL.GetUniformLocation(_program, "uFlowGain");
            _uFreshMix = GL.GetUniformLocation(_program, "uFreshMix");
            _uFeedbackDecay = GL.GetUniformLocation(_program, "uFeedbackDecay");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uCurrentTexture, 0);
            GL.Uniform1(_uPrevInputTexture, 1);
            GL.Uniform1(_uPrevFeedback, 2);
            GL.UseProgram(0);

            for (var i = 0; i < 2; i++)
            {
                _feedbackTextures[i] = GL.GenTexture();
                _feedbackFbos[i] = GL.GenFramebuffer();
            }

            _prevInputTexture = GL.GenTexture();
        }

        public override void OnResize(int width, int height, VisualPipeline host)
        {
            EnsureResources(host);

            if (_feedbackTextures[0] == 0 || _feedbackTextures[1] == 0 || _prevInputTexture == 0 ||
                _feedbackFbos[0] == 0 || _feedbackFbos[1] == 0)
            {
                return;
            }

            if (width == _lastWidth && height == _lastHeight)
            {
                return;
            }

            _lastWidth = width;
            _lastHeight = height;

            for (var i = 0; i < 2; i++)
            {
                GL.BindTexture(TextureTarget.Texture2D, _feedbackTextures[i]);
                GL.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    PixelInternalFormat.Rgba,
                    width,
                    height,
                    0,
                    OpenTK.Graphics.OpenGL4.PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _feedbackFbos[i]);
                GL.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D,
                    _feedbackTextures[i],
                    0);

                var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                if (status != FramebufferErrorCode.FramebufferComplete)
                {
                    throw new InvalidOperationException($"CodecBleed framebuffer incomplete: {status}");
                }
            }

            GL.BindTexture(TextureTarget.Texture2D, _prevInputTexture);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba,
                width,
                height,
                0,
                OpenTK.Graphics.OpenGL4.PixelFormat.Rgba,
                PixelType.UnsignedByte,
                IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            _feedbackWriteIndex = 0;
            _hasFeedbackHistory = false;
            _hasPrevInput = false;
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            try
            {
                if (_lastWidth <= 0 || _lastHeight <= 0)
                {
                    return;
                }

                if (inputTexture == 0)
                {
                    var fallbackTexture = _hasFeedbackHistory ? _feedbackTextures[1 - _feedbackWriteIndex] : 0;
                    if (fallbackTexture != 0)
                    {
                        host.DrawFullscreen(host._blitProgram, fallbackTexture);
                    }
                    else
                    {
                        GL.ClearColor(0f, 0f, 0f, 1f);
                        GL.Clear(ClearBufferMask.ColorBufferBit);
                    }

                    return;
                }

                if (!_hasFeedbackHistory)
                {
                    host.CopyTexture(inputTexture, _feedbackTextures[0]);
                    host.CopyTexture(inputTexture, _feedbackTextures[1]);
                    host.CopyTexture(inputTexture, _prevInputTexture);
                    _hasFeedbackHistory = true;
                    _hasPrevInput = true;
                }

                var writeIndex = _feedbackWriteIndex;
                var readIndex = 1 - writeIndex;

                GL.GetInteger(GetPName.DrawFramebufferBinding, out var previousFramebuffer);

                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _feedbackFbos[writeIndex]);
                GL.Viewport(0, 0, _lastWidth, _lastHeight);

                GL.UseProgram(_program);
                GL.Uniform1(_uIntensity, _intensity.CurrentValue);
                GL.Uniform1(_uFlowGain, _flowGain.CurrentValue);
                GL.Uniform1(_uFreshMix, _freshMix.CurrentValue);
                GL.Uniform1(_uFeedbackDecay, _feedbackDecay.CurrentValue);
                GL.Uniform1(_uMix, _mix.CurrentValue);

                host.DrawFullscreenWithTextures(
                    _program,
                    (0, inputTexture),
                    (1, _hasPrevInput ? _prevInputTexture : inputTexture),
                    (2, _feedbackTextures[readIndex]));

                GL.BindFramebuffer(FramebufferTarget.Framebuffer, previousFramebuffer);
                GL.Viewport(0, 0, _lastWidth, _lastHeight);
                host.DrawFullscreen(host._blitProgram, _feedbackTextures[writeIndex]);

                host.CopyTexture(inputTexture, _prevInputTexture);
                _hasPrevInput = true;

                _feedbackWriteIndex = readIndex;
            }
            catch (Exception)
            {
                if (inputTexture != 0)
                {
                    host.DrawFullscreen(host._blitProgram, inputTexture);
                }
                else
                {
                    GL.ClearColor(0f, 0f, 0f, 1f);
                    GL.Clear(ClearBufferMask.ColorBufferBit);
                }
            }
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }

            for (var i = 0; i < 2; i++)
            {
                if (_feedbackFbos[i] != 0)
                {
                    GL.DeleteFramebuffer(_feedbackFbos[i]);
                    _feedbackFbos[i] = 0;
                }

                if (_feedbackTextures[i] != 0)
                {
                    GL.DeleteTexture(_feedbackTextures[i]);
                    _feedbackTextures[i] = 0;
                }
            }

            if (_prevInputTexture != 0)
            {
                GL.DeleteTexture(_prevInputTexture);
                _prevInputTexture = 0;
            }
        }
    }
}
