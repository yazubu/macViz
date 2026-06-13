using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class EdgeDetectEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.edgeDetect";
        private readonly Parameter<float> _edgeStrength = new("Edge Effect / Strength", 0f, 5f, 1.4f);
        private readonly Parameter<float> _threshold = new("Edge Effect / Threshold", 0f, 1f, 0.25f);
        private readonly Parameter<float> _mix = new("Edge Effect / Mix", 0f, 1f, 1f);
        private readonly Parameter<float> _invert = new("Edge Effect / Invert", 0f, 1f, 0f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uEdgeStrength;
        private int _uThreshold;
        private int _uMix;
        private int _uInvert;

        public EdgeDetectEffectStage()
        {
            _parameters = [_edgeStrength, _threshold, _mix, _invert];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Edge Detection";
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
                uniform float uEdgeStrength;
                uniform float uThreshold;
                uniform float uMix;
                uniform float uInvert;

                float lumaAt(vec2 uv)
                {
                    vec3 c = texture(uTexture, uv).rgb;
                    return dot(c, vec3(0.299, 0.587, 0.114));
                }

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;
                    vec2 texSize = vec2(textureSize(uTexture, 0));
                    vec2 texel = 1.0 / max(texSize, vec2(1.0));

                    float tl = lumaAt(vUv + vec2(-texel.x,  texel.y));
                    float tc = lumaAt(vUv + vec2( 0.0,      texel.y));
                    float tr = lumaAt(vUv + vec2( texel.x,  texel.y));
                    float ml = lumaAt(vUv + vec2(-texel.x,  0.0));
                    float mr = lumaAt(vUv + vec2( texel.x,  0.0));
                    float bl = lumaAt(vUv + vec2(-texel.x, -texel.y));
                    float bc = lumaAt(vUv + vec2( 0.0,     -texel.y));
                    float br = lumaAt(vUv + vec2( texel.x, -texel.y));

                    float gx = -tl + tr - 2.0 * ml + 2.0 * mr - bl + br;
                    float gy =  tl + 2.0 * tc + tr - bl - 2.0 * bc - br;

                    float edge = length(vec2(gx, gy)) * max(uEdgeStrength, 0.0);
                    float t = clamp(uThreshold, 0.0, 1.0);
                    float edgeMask = smoothstep(t, min(1.0, t + 0.25), edge);
                    edgeMask = mix(edgeMask, 1.0 - edgeMask, clamp(uInvert, 0.0, 1.0));

                    vec3 edgeColor = vec3(edgeMask);
                    vec3 finalColor = mix(original, edgeColor, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(finalColor, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uEdgeStrength = GL.GetUniformLocation(_program, "uEdgeStrength");
            _uThreshold = GL.GetUniformLocation(_program, "uThreshold");
            _uMix = GL.GetUniformLocation(_program, "uMix");
            _uInvert = GL.GetUniformLocation(_program, "uInvert");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uEdgeStrength, _edgeStrength.CurrentValue);
            GL.Uniform1(_uThreshold, _threshold.CurrentValue);
            GL.Uniform1(_uMix, _mix.CurrentValue);
            GL.Uniform1(_uInvert, _invert.CurrentValue);

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
