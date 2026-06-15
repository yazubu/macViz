using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class InfinityMirrorEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.infinityMirror";

        private readonly Parameter<float> _centerX = new("Infinity Mirror / Center X", 0f, 1f, 0.5f);
        private readonly Parameter<float> _centerY = new("Infinity Mirror / Center Y", 0f, 1f, 0.5f);
        private readonly Parameter<float> _zoomStep = new("Infinity Mirror / Zoom Step", 1.001f, 1.8f, 1.12f);
        private readonly Parameter<float> _rotationStepDegrees = new("Infinity Mirror / Rotation Step (deg)", -45f, 45f, 2.5f);
        private readonly Parameter<float> _driftX = new("Infinity Mirror / Drift X", -0.2f, 0.2f, 0f);
        private readonly Parameter<float> _driftY = new("Infinity Mirror / Drift Y", -0.2f, 0.2f, -0.01f);
        private readonly Parameter<int> _iterations = new("Infinity Mirror / Iterations", 1, 32, 12);
        private readonly Parameter<float> _decay = new("Infinity Mirror / Decay", 0f, 1f, 0.84f);
        private readonly Parameter<float> _tunnelGain = new("Infinity Mirror / Tunnel Gain", 0f, 3f, 1.15f);
        private readonly Parameter<float> _mix = new("Infinity Mirror / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uCenter;
        private int _uZoomStep;
        private int _uRotationStepDegrees;
        private int _uDrift;
        private int _uIterations;
        private int _uDecay;
        private int _uTunnelGain;
        private int _uMix;

        public InfinityMirrorEffectStage()
        {
            _parameters =
            [
                _centerX,
                _centerY,
                _zoomStep,
                _rotationStepDegrees,
                _driftX,
                _driftY,
                _iterations,
                _decay,
                _tunnelGain,
                _mix
            ];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Infinity Mirror";
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
                uniform vec2 uCenter;
                uniform float uZoomStep;
                uniform float uRotationStepDegrees;
                uniform vec2 uDrift;
                uniform int uIterations;
                uniform float uDecay;
                uniform float uTunnelGain;
                uniform float uMix;

                const int MAX_ITERATIONS = 32;

                vec2 rotate(vec2 p, float a)
                {
                    float c = cos(a);
                    float s = sin(a);
                    return vec2(c * p.x - s * p.y, s * p.x + c * p.y);
                }

                float insideRect(vec2 uv)
                {
                    float insideX = step(0.0, uv.x) * step(uv.x, 1.0);
                    float insideY = step(0.0, uv.y) * step(uv.y, 1.0);
                    return insideX * insideY;
                }

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;

                    int iterations = clamp(uIterations, 1, MAX_ITERATIONS);
                    float zoomStep = max(uZoomStep, 1.0001);
                    float decay = clamp(uDecay, 0.0, 1.0);
                    float rotationStep = radians(uRotationStepDegrees);

                    vec3 accum = original;
                    float weightSum = 1.0;

                    vec2 uv = vUv;
                    float weight = 1.0;

                    for (int i = 0; i < MAX_ITERATIONS; i++)
                    {
                        if (i >= iterations)
                        {
                            break;
                        }

                        vec2 p = uv - uCenter;
                        p = rotate(p, rotationStep);
                        p = p / zoomStep;
                        p -= uDrift;
                        uv = uCenter + p;

                        float inside = insideRect(uv);
                        vec3 sampleColor = texture(uTexture, clamp(uv, vec2(0.0), vec2(1.0))).rgb;

                        weight *= decay;
                        float w = weight * inside;

                        accum += sampleColor * w;
                        weightSum += w;
                    }

                    vec3 mirror = (accum / max(weightSum, 1e-5)) * max(uTunnelGain, 0.0);
                    vec3 color = mix(original, mirror, clamp(uMix, 0.0, 1.0));

                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uCenter = GL.GetUniformLocation(_program, "uCenter");
            _uZoomStep = GL.GetUniformLocation(_program, "uZoomStep");
            _uRotationStepDegrees = GL.GetUniformLocation(_program, "uRotationStepDegrees");
            _uDrift = GL.GetUniformLocation(_program, "uDrift");
            _uIterations = GL.GetUniformLocation(_program, "uIterations");
            _uDecay = GL.GetUniformLocation(_program, "uDecay");
            _uTunnelGain = GL.GetUniformLocation(_program, "uTunnelGain");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform2(_uCenter, _centerX.CurrentValue, _centerY.CurrentValue);
            GL.Uniform1(_uZoomStep, _zoomStep.CurrentValue);
            GL.Uniform1(_uRotationStepDegrees, _rotationStepDegrees.CurrentValue);
            GL.Uniform2(_uDrift, _driftX.CurrentValue, _driftY.CurrentValue);
            GL.Uniform1(_uIterations, _iterations.CurrentValue);
            GL.Uniform1(_uDecay, _decay.CurrentValue);
            GL.Uniform1(_uTunnelGain, _tunnelGain.CurrentValue);
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
