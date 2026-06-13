using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class PixelateEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.pixelate";
        private readonly Parameter<float> _pixelSize = new("Pixelate Effect / Pixel Size", 1f, 128f, 8f);
        private readonly Parameter<float> _mix = new("Pixelate Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uPixelSize;
        private int _uMix;

        public PixelateEffectStage()
        {
            _parameters = [_pixelSize, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Pixelate";
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
                uniform float uPixelSize;
                uniform float uMix;

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;

                    vec2 texSize = vec2(textureSize(uTexture, 0));
                    vec2 safeSize = max(texSize, vec2(1.0));
                    float block = max(uPixelSize, 1.0);

                    vec2 px = vUv * safeSize;
                    vec2 snapped = (floor(px / block) + 0.5) * block;
                    vec2 uv = clamp(snapped / safeSize, vec2(0.0), vec2(1.0));

                    vec3 pixelated = texture(uTexture, uv).rgb;
                    vec3 color = mix(original, pixelated, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uPixelSize = GL.GetUniformLocation(_program, "uPixelSize");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uPixelSize, _pixelSize.CurrentValue);
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
