using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class ColorSwapEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.colorSwap";
        private readonly Parameter<int> _mode = new("Color Swap Effect / Mode", 0, 5, 0);
        private readonly Parameter<float> _mix = new("Color Swap Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uMode;
        private int _uMix;

        public ColorSwapEffectStage()
        {
            _parameters = [_mode, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Color Swap";
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
                uniform int uMode;
                uniform float uMix;

                vec3 swapChannels(vec3 c, int mode)
                {
                    if (mode == 1) return c.rbg;
                    if (mode == 2) return c.grb;
                    if (mode == 3) return c.gbr;
                    if (mode == 4) return c.brg;
                    if (mode == 5) return c.bgr;
                    return c.rgb;
                }

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;
                    int mode = clamp(uMode, 0, 5);
                    vec3 swapped = swapChannels(original, mode);
                    vec3 color = mix(original, swapped, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uMode = GL.GetUniformLocation(_program, "uMode");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uMode, _mode.CurrentValue);
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
