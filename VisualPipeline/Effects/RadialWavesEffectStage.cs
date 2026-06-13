using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class RadialWavesEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.radialWaves";

        private readonly Parameter<float> _waveLength = new("Radial Waves / Wave Length (px)", 4f, 1024f, 96f);
        private readonly Parameter<float> _wavePhase = new("Radial Waves / Wave Phase (rad)", -62.8319f, 62.8319f, 0f);
        private readonly Parameter<float> _centerX = new("Radial Waves / Center X", 0f, 1f, 0.5f);
        private readonly Parameter<float> _centerY = new("Radial Waves / Center Y", 0f, 1f, 0.5f);
        private readonly Parameter<float> _amplitude = new("Radial Waves / Amplitude (px)", 0f, 64f, 8f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uWaveLength;
        private int _uWavePhase;
        private int _uCenter;
        private int _uAmplitude;

        public RadialWavesEffectStage()
        {
            _parameters = [_waveLength, _wavePhase, _centerX, _centerY, _amplitude];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Radial Waves";
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
                uniform float uWaveLength;
                uniform float uWavePhase;
                uniform vec2 uCenter;
                uniform float uAmplitude;

                const float TAU = 6.28318530718;

                void main()
                {
                    vec2 texSize = vec2(textureSize(uTexture, 0));
                    vec2 safeTexSize = max(texSize, vec2(1.0));
                    vec2 texel = 1.0 / safeTexSize;

                    vec2 p = vUv - uCenter;
                    float radiusPx = length(p * safeTexSize);

                    vec2 dir = radiusPx > 0.0001 ? normalize(p) : vec2(0.0, 0.0);
                    float waveLength = max(uWaveLength, 0.001);
                    float wave = sin((radiusPx / waveLength) * TAU + uWavePhase);

                    vec2 displacedUv = vUv + dir * wave * max(uAmplitude, 0.0) * texel;
                    vec2 sampleUv = clamp(displacedUv, vec2(0.0), vec2(1.0));

                    fragColor = texture(uTexture, sampleUv);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uWaveLength = GL.GetUniformLocation(_program, "uWaveLength");
            _uWavePhase = GL.GetUniformLocation(_program, "uWavePhase");
            _uCenter = GL.GetUniformLocation(_program, "uCenter");
            _uAmplitude = GL.GetUniformLocation(_program, "uAmplitude");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uWaveLength, _waveLength.CurrentValue);
            GL.Uniform1(_uWavePhase, _wavePhase.CurrentValue);
            GL.Uniform2(_uCenter, _centerX.CurrentValue, _centerY.CurrentValue);
            GL.Uniform1(_uAmplitude, _amplitude.CurrentValue);

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
