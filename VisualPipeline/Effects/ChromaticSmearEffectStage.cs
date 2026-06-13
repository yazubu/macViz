using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class ChromaticSmearEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.chromaticSmear";

        private readonly Parameter<float> _offsetPixels = new("Chromatic Smear / Channel Offset (px)", 0f, 96f, 10f);
        private readonly Parameter<float> _motionGain = new("Chromatic Smear / Motion Gain", 0f, 6f, 1.25f);
        private readonly Parameter<float> _motionThreshold = new("Chromatic Smear / Motion Threshold", 0f, 1f, 0.07f);
        private readonly Parameter<float> _motionSoftness = new("Chromatic Smear / Motion Softness", 0.0001f, 0.5f, 0.12f);
        private readonly Parameter<float> _feedbackDecay = new("Chromatic Smear / Feedback Decay", 0f, 1f, 0.985f);
        private readonly Parameter<float> _freshMix = new("Chromatic Smear / Fresh Mix", 0f, 1f, 0.16f);
        private readonly Parameter<float> _smearAmount = new("Chromatic Smear / Smear Amount", 0f, 2f, 1f);
        private readonly Parameter<float> _mix = new("Chromatic Smear / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uCurrentTexture;
        private int _uPrevInputTexture;
        private int _uPrevFeedback;
        private int _uOffsetPixels;
        private int _uMotionGain;
        private int _uMotionThreshold;
        private int _uMotionSoftness;
        private int _uFeedbackDecay;
        private int _uFreshMix;
        private int _uSmearAmount;
        private int _uMix;

        private readonly int[] _feedbackTextures = new int[2];
        private readonly int[] _feedbackFbos = new int[2];
        private int _feedbackWriteIndex;
        private bool _hasFeedbackHistory;

        private int _prevInputTexture;
        private bool _hasPrevInput;

        private int _lastWidth;
        private int _lastHeight;

        public ChromaticSmearEffectStage()
        {
            _parameters =
            [
                _offsetPixels,
                _motionGain,
                _motionThreshold,
                _motionSoftness,
                _feedbackDecay,
                _freshMix,
                _smearAmount,
                _mix
            ];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Chromatic Smear (Prism Drag)";
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
                uniform float uOffsetPixels;
                uniform float uMotionGain;
                uniform float uMotionThreshold;
                uniform float uMotionSoftness;
                uniform float uFeedbackDecay;
                uniform float uFreshMix;
                uniform float uSmearAmount;
                uniform float uMix;

                float luma(vec3 c)
                {
                    return dot(c, vec3(0.299, 0.587, 0.114));
                }

                float lumaDelta(vec2 uv)
                {
                    vec2 clampedUv = clamp(uv, vec2(0.0), vec2(1.0));
                    float curr = luma(texture(uCurrentTexture, clampedUv).rgb);
                    float prev = luma(texture(uPrevInputTexture, clampedUv).rgb);
                    return curr - prev;
                }

                void main()
                {
                    vec3 currentColor = texture(uCurrentTexture, vUv).rgb;
                    vec3 previousFeedbackCenter = texture(uPrevFeedback, vUv).rgb;

                    vec2 texSize = vec2(textureSize(uCurrentTexture, 0));
                    vec2 safeTexSize = max(texSize, vec2(1.0));
                    vec2 texel = 1.0 / safeTexSize;

                    vec3 prevInputColor = texture(uPrevInputTexture, vUv).rgb;
                    float motionRaw = luma(abs(currentColor - prevInputColor)) * max(uMotionGain, 0.0);
                    float motionMask = smoothstep(
                        clamp(uMotionThreshold, 0.0, 1.0),
                        clamp(uMotionThreshold + max(uMotionSoftness, 0.0001), 0.0, 1.0),
                        clamp(motionRaw, 0.0, 1.0));

                    float dx = lumaDelta(vUv + vec2(texel.x, 0.0)) - lumaDelta(vUv - vec2(texel.x, 0.0));
                    float dy = lumaDelta(vUv + vec2(0.0, texel.y)) - lumaDelta(vUv - vec2(0.0, texel.y));
                    vec2 motionDir = normalize(vec2(dx, dy) + vec2(1e-5));
                    vec2 channelOffsetUv = motionDir * uOffsetPixels * texel;

                    vec2 uvR = clamp(vUv + channelOffsetUv, vec2(0.0), vec2(1.0));
                    vec2 uvG = vUv;
                    vec2 uvB = clamp(vUv - channelOffsetUv, vec2(0.0), vec2(1.0));

                    vec3 chromaSmear = vec3(
                        texture(uPrevFeedback, uvR).r,
                        texture(uPrevFeedback, uvG).g,
                        texture(uPrevFeedback, uvB).b);

                    float smearMix = clamp(motionMask * max(uSmearAmount, 0.0), 0.0, 1.0);
                    vec3 decayedFeedback = previousFeedbackCenter * clamp(uFeedbackDecay, 0.0, 1.0);
                    vec3 feedbackBase = mix(decayedFeedback, currentColor, clamp(uFreshMix, 0.0, 1.0));
                    vec3 smeared = mix(feedbackBase, chromaSmear, smearMix);
                    vec3 color = mix(currentColor, smeared, clamp(uMix, 0.0, 1.0));

                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uCurrentTexture = GL.GetUniformLocation(_program, "uCurrentTexture");
            _uPrevInputTexture = GL.GetUniformLocation(_program, "uPrevInputTexture");
            _uPrevFeedback = GL.GetUniformLocation(_program, "uPrevFeedback");
            _uOffsetPixels = GL.GetUniformLocation(_program, "uOffsetPixels");
            _uMotionGain = GL.GetUniformLocation(_program, "uMotionGain");
            _uMotionThreshold = GL.GetUniformLocation(_program, "uMotionThreshold");
            _uMotionSoftness = GL.GetUniformLocation(_program, "uMotionSoftness");
            _uFeedbackDecay = GL.GetUniformLocation(_program, "uFeedbackDecay");
            _uFreshMix = GL.GetUniformLocation(_program, "uFreshMix");
            _uSmearAmount = GL.GetUniformLocation(_program, "uSmearAmount");
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
                    throw new InvalidOperationException($"ChromaticSmear framebuffer incomplete: {status}");
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
                GL.Uniform1(_uOffsetPixels, _offsetPixels.CurrentValue);
                GL.Uniform1(_uMotionGain, _motionGain.CurrentValue);
                GL.Uniform1(_uMotionThreshold, _motionThreshold.CurrentValue);
                GL.Uniform1(_uMotionSoftness, _motionSoftness.CurrentValue);
                GL.Uniform1(_uFeedbackDecay, _feedbackDecay.CurrentValue);
                GL.Uniform1(_uFreshMix, _freshMix.CurrentValue);
                GL.Uniform1(_uSmearAmount, _smearAmount.CurrentValue);
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
