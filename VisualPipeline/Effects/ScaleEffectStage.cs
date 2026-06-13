using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class ScaleEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.scale";
        private readonly Parameter<float> _scale = new("Scale Effect / Scale", 0.2f, 3f, 1f);
        private readonly Parameter<float> _pivotX = new("Scale Effect / Pivot X", 0f, 1f, 0.5f);
        private readonly Parameter<float> _pivotY = new("Scale Effect / Pivot Y", 0f, 1f, 0.5f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uScale;
        private int _uPivot;

        public ScaleEffectStage()
        {
            _parameters = [_scale, _pivotX, _pivotY];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Scale";
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
                uniform float uScale;
                uniform vec2 uPivot;

                void main()
                {
                    float s = max(uScale, 0.001);
                    vec2 uv = ((vUv - uPivot) / s) + uPivot;
                    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
                    {
                        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
                        return;
                    }

                    fragColor = texture(uTexture, uv);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uScale = GL.GetUniformLocation(_program, "uScale");
            _uPivot = GL.GetUniformLocation(_program, "uPivot");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uScale, _scale.CurrentValue);
            GL.Uniform2(_uPivot, _pivotX.CurrentValue, _pivotY.CurrentValue);

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
