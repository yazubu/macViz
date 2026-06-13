using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class MotionBlurEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.motionBlur";
        private readonly Parameter<float> _directionRadians = new("Motion Blur Effect / Direction (rad)", -6.28319f, 6.28319f, 0f);
        private readonly Parameter<float> _distancePixels = new("Motion Blur Effect / Distance (px)", 0f, 256f, 24f);
        private readonly Parameter<int> _samples = new("Motion Blur Effect / Samples", 1, 48, 12);
        private readonly Parameter<float> _mix = new("Motion Blur Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uDirectionRadians;
        private int _uDistancePixels;
        private int _uSamples;
        private int _uMix;

        public MotionBlurEffectStage()
        {
            _parameters = [_directionRadians, _distancePixels, _samples, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Motion Blur";
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

                uniform sampler2D uTexture;
                uniform float uDirectionRadians;
                uniform float uDistancePixels;
                uniform int uSamples;
                uniform float uMix;

                const int MAX_SAMPLES = 64;

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;

                    vec2 texSize = vec2(textureSize(uTexture, 0));
                    vec2 texel = 1.0 / max(texSize, vec2(1.0));
                    vec2 dir = vec2(cos(uDirectionRadians), sin(uDirectionRadians));
                    vec2 motion = dir * uDistancePixels * texel;

                    int sampleCount = clamp(uSamples, 1, MAX_SAMPLES);
                    vec3 acc = vec3(0.0);

                    for (int i = 0; i < MAX_SAMPLES; i++)
                    {
                        if (i >= sampleCount)
                        {
                            break;
                        }

                        float t = sampleCount <= 1 ? 0.0 : (float(i) / float(sampleCount - 1)) * 2.0 - 1.0;
                        vec2 uv = vUv + motion * t;
                        acc += texture(uTexture, clamp(uv, vec2(0.0), vec2(1.0))).rgb;
                    }

                    vec3 blurred = acc / float(sampleCount);
                    vec3 color = mix(original, blurred, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uDirectionRadians = GL.GetUniformLocation(_program, "uDirectionRadians");
            _uDistancePixels = GL.GetUniformLocation(_program, "uDistancePixels");
            _uSamples = GL.GetUniformLocation(_program, "uSamples");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uDirectionRadians, _directionRadians.CurrentValue);
            GL.Uniform1(_uDistancePixels, _distancePixels.CurrentValue);
            GL.Uniform1(_uSamples, _samples.CurrentValue);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            host.DrawFullscreen(_program, inputTexture);
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }
        }
    }
}
