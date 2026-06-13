using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class ColorShiftEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.colorShift";
        private readonly Parameter<float> _redShiftPixels = new("Color Shift Effect / Red Shift (px)", -256f, 256f, 8f);
        private readonly Parameter<float> _greenShiftPixels = new("Color Shift Effect / Green Shift (px)", -256f, 256f, 0f);
        private readonly Parameter<float> _blueShiftPixels = new("Color Shift Effect / Blue Shift (px)", -256f, 256f, -8f);
        private readonly Parameter<float> _directionRadians = new("Color Shift Effect / Direction (rad)", -6.28319f, 6.28319f, 0f);
        private readonly Parameter<float> _mix = new("Color Shift Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uRedShiftPixels;
        private int _uGreenShiftPixels;
        private int _uBlueShiftPixels;
        private int _uDirectionRadians;
        private int _uMix;

        public ColorShiftEffectStage()
        {
            _parameters = [_redShiftPixels, _greenShiftPixels, _blueShiftPixels, _directionRadians, _mix];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Color Shift";
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
                uniform float uRedShiftPixels;
                uniform float uGreenShiftPixels;
                uniform float uBlueShiftPixels;
                uniform float uDirectionRadians;
                uniform float uMix;

                vec2 offsetForShift(float pixels, vec2 texel, vec2 dir)
                {
                    return dir * pixels * texel;
                }

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;

                    vec2 texSize = vec2(textureSize(uTexture, 0));
                    vec2 texel = 1.0 / max(texSize, vec2(1.0));
                    vec2 dir = vec2(cos(uDirectionRadians), sin(uDirectionRadians));

                    vec2 uvR = vUv + offsetForShift(uRedShiftPixels, texel, dir);
                    vec2 uvG = vUv + offsetForShift(uGreenShiftPixels, texel, dir);
                    vec2 uvB = vUv + offsetForShift(uBlueShiftPixels, texel, dir);

                    vec3 shifted = vec3(
                        texture(uTexture, uvR).r,
                        texture(uTexture, uvG).g,
                        texture(uTexture, uvB).b
                    );

                    vec3 color = mix(original, shifted, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uRedShiftPixels = GL.GetUniformLocation(_program, "uRedShiftPixels");
            _uGreenShiftPixels = GL.GetUniformLocation(_program, "uGreenShiftPixels");
            _uBlueShiftPixels = GL.GetUniformLocation(_program, "uBlueShiftPixels");
            _uDirectionRadians = GL.GetUniformLocation(_program, "uDirectionRadians");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uRedShiftPixels, _redShiftPixels.CurrentValue);
            GL.Uniform1(_uGreenShiftPixels, _greenShiftPixels.CurrentValue);
            GL.Uniform1(_uBlueShiftPixels, _blueShiftPixels.CurrentValue);
            GL.Uniform1(_uDirectionRadians, _directionRadians.CurrentValue);
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
