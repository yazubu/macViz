using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class KaleidoscopeEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.kaleidoscope";
        private readonly Parameter<int> _axisCount = new("Kaleidoscope Effect / Axis Count", 1, 24, 6);
        private readonly Parameter<float> _centerX = new("Kaleidoscope Effect / Center X", 0f, 1f, 0.5f);
        private readonly Parameter<float> _centerY = new("Kaleidoscope Effect / Center Y", 0f, 1f, 0.5f);
        private readonly Parameter<float> _axisRotation = new("Kaleidoscope Effect / Axis Rotation (rad)", -6.28319f, 6.28319f, 0f);
        private readonly Parameter<float> _radialScale = new("Kaleidoscope Effect / Radial Scale", 0.2f, 3f, 1f);
        private readonly Parameter<float> _mix = new("Kaleidoscope Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uAxisCount;
        private int _uCenter;
        private int _uAxisRotation;
        private int _uRadialScale;
        private int _uMix;

        public KaleidoscopeEffectStage()
        {
            _parameters = [_axisCount, _centerX, _centerY, _axisRotation, _radialScale, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Kaleidoscope";
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
                uniform int uAxisCount;
                uniform vec2 uCenter;
                uniform float uAxisRotation;
                uniform float uRadialScale;
                uniform float uMix;

                const float TAU = 6.28318530718;

                vec2 kaleidoscopeUv(vec2 uv)
                {
                    vec2 p = uv - uCenter;
                    float radius = length(p);

                    if (radius <= 0.000001)
                    {
                        return uCenter;
                    }

                    float axisCount = max(float(uAxisCount), 1.0);
                    float segment = TAU / axisCount;

                    float angle = atan(p.y, p.x) - uAxisRotation;
                    angle = mod(angle + 0.5 * segment, segment) - 0.5 * segment;
                    angle = abs(angle);

                    float r = radius / max(uRadialScale, 0.0001);
                    vec2 mirrored = vec2(cos(angle + uAxisRotation), sin(angle + uAxisRotation)) * r;
                    return uCenter + mirrored;
                }

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;
                    vec2 sampledUv = kaleidoscopeUv(vUv);
                    vec3 kaleidoscope = texture(uTexture, sampledUv).rgb;

                    vec3 color = mix(original, kaleidoscope, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uAxisCount = GL.GetUniformLocation(_program, "uAxisCount");
            _uCenter = GL.GetUniformLocation(_program, "uCenter");
            _uAxisRotation = GL.GetUniformLocation(_program, "uAxisRotation");
            _uRadialScale = GL.GetUniformLocation(_program, "uRadialScale");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uAxisCount, _axisCount.CurrentValue);
            GL.Uniform2(_uCenter, _centerX.CurrentValue, _centerY.CurrentValue);
            GL.Uniform1(_uAxisRotation, _axisRotation.CurrentValue);
            GL.Uniform1(_uRadialScale, _radialScale.CurrentValue);
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
