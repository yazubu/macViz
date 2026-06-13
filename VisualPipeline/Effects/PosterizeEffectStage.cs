using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class PosterizeEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.posterize";
        private readonly Parameter<int> _levels = new("Posterize Effect / Levels", 2, 32, 6);
        private readonly Parameter<float> _gamma = new("Posterize Effect / Gamma", 0.1f, 3f, 1f);
        private readonly Parameter<float> _mix = new("Posterize Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uLevels;
        private int _uGamma;
        private int _uMix;

        public PosterizeEffectStage()
        {
            _parameters = [_levels, _gamma, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Posterize";
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
                uniform int uLevels;
                uniform float uGamma;
                uniform float uMix;

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;

                    float levels = max(float(uLevels), 2.0);
                    float gamma = max(uGamma, 0.001);

                    vec3 corrected = pow(max(original, vec3(0.0)), vec3(gamma));
                    vec3 quantized = floor(corrected * (levels - 1.0) + 0.5) / (levels - 1.0);
                    vec3 posterized = pow(max(quantized, vec3(0.0)), vec3(1.0 / gamma));

                    vec3 color = mix(original, posterized, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uLevels = GL.GetUniformLocation(_program, "uLevels");
            _uGamma = GL.GetUniformLocation(_program, "uGamma");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uLevels, _levels.CurrentValue);
            GL.Uniform1(_uGamma, _gamma.CurrentValue);
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
