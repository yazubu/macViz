using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class FlipEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.flip";

        private readonly Parameter<float> _flipX = new("Flip Effect / Flip X", 0f, 1f, 0f);
        private readonly Parameter<float> _flipY = new("Flip Effect / Flip Y", 0f, 1f, 0f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uFlipX;
        private int _uFlipY;

        public FlipEffectStage()
        {
            _parameters = [_flipX, _flipY];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Flip";
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
                uniform float uFlipX;
                uniform float uFlipY;

                void main()
                {
                    vec2 uv = vUv;
                    if (uFlipX >= 0.5)
                    {
                        uv.x = 1.0 - uv.x;
                    }

                    if (uFlipY >= 0.5)
                    {
                        uv.y = 1.0 - uv.y;
                    }

                    fragColor = texture(uTexture, uv);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uFlipX = GL.GetUniformLocation(_program, "uFlipX");
            _uFlipY = GL.GetUniformLocation(_program, "uFlipY");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uFlipX, _flipX.CurrentValue);
            GL.Uniform1(_uFlipY, _flipY.CurrentValue);

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
