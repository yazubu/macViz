using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class MotionIsolateEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.motionIsolate";

        private readonly Parameter<float> _motionGain = new("Motion Isolate / Motion Gain", 0f, 8f, 1.6f);
        private readonly Parameter<float> _threshold = new("Motion Isolate / Threshold", 0f, 1f, 0.08f);
        private readonly Parameter<float> _softness = new("Motion Isolate / Softness", 0.0001f, 0.5f, 0.12f);
        private readonly Parameter<float> _maskOnly = new("Motion Isolate / Mask Only", 0f, 1f, 0f);
        private readonly Parameter<float> _mix = new("Motion Isolate / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uCurrentTexture;
        private int _uPrevTexture;
        private int _uMotionGain;
        private int _uThreshold;
        private int _uSoftness;
        private int _uMaskOnly;
        private int _uMix;

        private int _prevInputTexture;
        private bool _hasPrevInput;
        private int _lastWidth;
        private int _lastHeight;

        public MotionIsolateEffectStage()
        {
            _parameters = [_motionGain, _threshold, _softness, _maskOnly, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Motion Isolate";
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
                uniform sampler2D uPrevTexture;
                uniform float uMotionGain;
                uniform float uThreshold;
                uniform float uSoftness;
                uniform float uMaskOnly;
                uniform float uMix;

                float luma(vec3 c)
                {
                    return dot(c, vec3(0.299, 0.587, 0.114));
                }

                void main()
                {
                    vec3 currentColor = texture(uCurrentTexture, vUv).rgb;
                    vec3 prevColor = texture(uPrevTexture, vUv).rgb;

                    vec3 diff = abs(currentColor - prevColor);
                    float motionRaw = luma(diff) * max(uMotionGain, 0.0);

                    float threshold = clamp(uThreshold, 0.0, 1.0);
                    float softness = max(uSoftness, 0.0001);
                    float motionMask = smoothstep(threshold, min(1.0, threshold + softness), clamp(motionRaw, 0.0, 1.0));

                    vec3 movingPixels = currentColor * motionMask;
                    vec3 maskPreview = vec3(motionMask);
                    vec3 isolated = mix(movingPixels, maskPreview, clamp(uMaskOnly, 0.0, 1.0));

                    vec3 color = mix(currentColor, isolated, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uCurrentTexture = GL.GetUniformLocation(_program, "uCurrentTexture");
            _uPrevTexture = GL.GetUniformLocation(_program, "uPrevTexture");
            _uMotionGain = GL.GetUniformLocation(_program, "uMotionGain");
            _uThreshold = GL.GetUniformLocation(_program, "uThreshold");
            _uSoftness = GL.GetUniformLocation(_program, "uSoftness");
            _uMaskOnly = GL.GetUniformLocation(_program, "uMaskOnly");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uCurrentTexture, 0);
            GL.Uniform1(_uPrevTexture, 1);
            GL.UseProgram(0);

            _prevInputTexture = GL.GenTexture();
        }

        public override void OnResize(int width, int height, VisualPipeline host)
        {
            EnsureResources(host);

            if (_prevInputTexture == 0)
            {
                return;
            }

            if (width == _lastWidth && height == _lastHeight)
            {
                return;
            }

            _lastWidth = width;
            _lastHeight = height;

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

            _hasPrevInput = false;
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            if (_lastWidth <= 0 || _lastHeight <= 0)
            {
                return;
            }

            if (inputTexture == 0)
            {
                GL.ClearColor(0f, 0f, 0f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                return;
            }

            if (!_hasPrevInput)
            {
                host.CopyTexture(inputTexture, _prevInputTexture);
                _hasPrevInput = true;

                GL.ClearColor(0f, 0f, 0f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                return;
            }

            GL.UseProgram(_program);
            GL.Uniform1(_uMotionGain, _motionGain.CurrentValue);
            GL.Uniform1(_uThreshold, _threshold.CurrentValue);
            GL.Uniform1(_uSoftness, _softness.CurrentValue);
            GL.Uniform1(_uMaskOnly, _maskOnly.CurrentValue);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            host.DrawFullscreenWithTextures(
                _program,
                (0, inputTexture),
                (1, _prevInputTexture));

            host.CopyTexture(inputTexture, _prevInputTexture);
            _hasPrevInput = true;
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }

            if (_prevInputTexture != 0)
            {
                GL.DeleteTexture(_prevInputTexture);
                _prevInputTexture = 0;
            }
        }
    }
}
