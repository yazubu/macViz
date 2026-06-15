using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class BleedingEdgeEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.bleedingEdge";

        private readonly Parameter<float> _edgeStrength = new("Bleeding Edge / Edge Strength", 0f, 6f, 2.1f);
        private readonly Parameter<float> _edgeThreshold = new("Bleeding Edge / Edge Threshold", 0f, 1f, 0.18f);
        private readonly Parameter<float> _edgeSoftness = new("Bleeding Edge / Edge Softness", 0.001f, 0.5f, 0.12f);
        private readonly Parameter<float> _bleedDistancePixels = new("Bleeding Edge / Bleed Distance (px)", 0f, 72f, 12f);
        private readonly Parameter<float> _flowGain = new("Bleeding Edge / Flow Gain", 0f, 6f, 1.75f);
        private readonly Parameter<float> _feedbackDecay = new("Bleeding Edge / Feedback Decay", 0.7f, 1f, 0.985f);
        private readonly Parameter<float> _freshMix = new("Bleeding Edge / Fresh Mix", 0f, 0.5f, 0.08f);
        private readonly Parameter<float> _edgeTint = new("Bleeding Edge / Edge Tint", 0f, 1f, 0.72f);
        private readonly Parameter<float> _mix = new("Bleeding Edge / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uCurrentTexture;
        private int _uPrevInputTexture;
        private int _uPrevFeedback;
        private int _uEdgeStrength;
        private int _uEdgeThreshold;
        private int _uEdgeSoftness;
        private int _uBleedDistancePixels;
        private int _uFlowGain;
        private int _uFeedbackDecay;
        private int _uFreshMix;
        private int _uEdgeTint;
        private int _uMix;

        private readonly PingPongFramebufferPair _feedbackBuffers = new();
        private readonly HistoryTexture2D _prevInput = new();
        private int _lastWidth;
        private int _lastHeight;

        public BleedingEdgeEffectStage()
        {
            _parameters =
            [
                _edgeStrength,
                _edgeThreshold,
                _edgeSoftness,
                _bleedDistancePixels,
                _flowGain,
                _feedbackDecay,
                _freshMix,
                _edgeTint,
                _mix
            ];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Bleeding Edge";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            _program = CompileProgram(VertexShaderSource, FragmentShaderSource);
            _uCurrentTexture = GL.GetUniformLocation(_program, "uCurrentTexture");
            _uPrevInputTexture = GL.GetUniformLocation(_program, "uPrevInputTexture");
            _uPrevFeedback = GL.GetUniformLocation(_program, "uPrevFeedback");
            _uEdgeStrength = GL.GetUniformLocation(_program, "uEdgeStrength");
            _uEdgeThreshold = GL.GetUniformLocation(_program, "uEdgeThreshold");
            _uEdgeSoftness = GL.GetUniformLocation(_program, "uEdgeSoftness");
            _uBleedDistancePixels = GL.GetUniformLocation(_program, "uBleedDistancePixels");
            _uFlowGain = GL.GetUniformLocation(_program, "uFlowGain");
            _uFeedbackDecay = GL.GetUniformLocation(_program, "uFeedbackDecay");
            _uFreshMix = GL.GetUniformLocation(_program, "uFreshMix");
            _uEdgeTint = GL.GetUniformLocation(_program, "uEdgeTint");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uCurrentTexture, 0);
            GL.Uniform1(_uPrevInputTexture, 1);
            GL.Uniform1(_uPrevFeedback, 2);
            GL.UseProgram(0);

            _feedbackBuffers.EnsureCreated();
            _prevInput.EnsureCreated();
        }

        public override void OnResize(int width, int height, VisualPipeline host)
        {
            EnsureResources(host);

            if (width == _lastWidth && height == _lastHeight)
            {
                return;
            }

            _lastWidth = width;
            _lastHeight = height;

            _feedbackBuffers.Resize(width, height, "BleedingEdge");
            _prevInput.Resize(width, height);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            try
            {
                if (_lastWidth <= 0 || _lastHeight <= 0)
                {
                    return;
                }

                if (inputTexture == 0)
                {
                    var fallbackTexture = _feedbackBuffers.HasHistory ? _feedbackBuffers.ReadTexture : 0;
                    if (fallbackTexture != 0)
                    {
                        host.DrawFullscreen(host._blitProgram, fallbackTexture);
                    }
                    else
                    {
                        GL.ClearColor(0f, 0f, 0f, 1f);
                        GL.Clear(ClearBufferMask.ColorBufferBit);
                    }

                    return;
                }

                if (!_feedbackBuffers.HasHistory)
                {
                    _feedbackBuffers.SeedFromTexture(host, inputTexture);
                }

                if (!_prevInput.HasData)
                {
                    _prevInput.CopyFrom(host, inputTexture);
                }

                GL.GetInteger(GetPName.DrawFramebufferBinding, out var previousFramebuffer);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _feedbackBuffers.WriteFbo);
                GL.Viewport(0, 0, _lastWidth, _lastHeight);

                GL.UseProgram(_program);
                GL.Uniform1(_uEdgeStrength, _edgeStrength.CurrentValue);
                GL.Uniform1(_uEdgeThreshold, _edgeThreshold.CurrentValue);
                GL.Uniform1(_uEdgeSoftness, _edgeSoftness.CurrentValue);
                GL.Uniform1(_uBleedDistancePixels, _bleedDistancePixels.CurrentValue);
                GL.Uniform1(_uFlowGain, _flowGain.CurrentValue);
                GL.Uniform1(_uFeedbackDecay, _feedbackDecay.CurrentValue);
                GL.Uniform1(_uFreshMix, _freshMix.CurrentValue);
                GL.Uniform1(_uEdgeTint, _edgeTint.CurrentValue);
                GL.Uniform1(_uMix, _mix.CurrentValue);

                host.DrawFullscreenWithTextures(
                    _program,
                    (0, inputTexture),
                    (1, _prevInput.TextureId),
                    (2, _feedbackBuffers.ReadTexture));

                GL.BindFramebuffer(FramebufferTarget.Framebuffer, previousFramebuffer);
                GL.Viewport(0, 0, _lastWidth, _lastHeight);
                host.DrawFullscreen(host._blitProgram, _feedbackBuffers.WriteTexture);

                _prevInput.CopyFrom(host, inputTexture);
                _feedbackBuffers.Advance();
            }
            catch
            {
                if (inputTexture != 0)
                {
                    host.DrawFullscreen(host._blitProgram, inputTexture);
                }
                else
                {
                    GL.ClearColor(0f, 0f, 0f, 1f);
                    GL.Clear(ClearBufferMask.ColorBufferBit);
                }
            }
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }

            _feedbackBuffers.Dispose();
            _prevInput.Dispose();
        }

        private const string VertexShaderSource = """
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

        private const string FragmentShaderSource = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColor;

            uniform sampler2D uCurrentTexture;
            uniform sampler2D uPrevInputTexture;
            uniform sampler2D uPrevFeedback;
            uniform float uEdgeStrength;
            uniform float uEdgeThreshold;
            uniform float uEdgeSoftness;
            uniform float uBleedDistancePixels;
            uniform float uFlowGain;
            uniform float uFeedbackDecay;
            uniform float uFreshMix;
            uniform float uEdgeTint;
            uniform float uMix;

            float luma(vec3 c)
            {
                return dot(c, vec3(0.299, 0.587, 0.114));
            }

            float lumaDelta(vec2 uv)
            {
                vec2 safeUv = clamp(uv, vec2(0.0), vec2(1.0));
                float curr = luma(texture(uCurrentTexture, safeUv).rgb);
                float prev = luma(texture(uPrevInputTexture, safeUv).rgb);
                return curr - prev;
            }

            vec2 sobel(vec2 uv, vec2 texel)
            {
                float tl = luma(texture(uCurrentTexture, clamp(uv + vec2(-texel.x,  texel.y), vec2(0.0), vec2(1.0))).rgb);
                float tc = luma(texture(uCurrentTexture, clamp(uv + vec2( 0.0,      texel.y), vec2(0.0), vec2(1.0))).rgb);
                float tr = luma(texture(uCurrentTexture, clamp(uv + vec2( texel.x,  texel.y), vec2(0.0), vec2(1.0))).rgb);
                float ml = luma(texture(uCurrentTexture, clamp(uv + vec2(-texel.x,  0.0), vec2(0.0), vec2(1.0))).rgb);
                float mr = luma(texture(uCurrentTexture, clamp(uv + vec2( texel.x,  0.0), vec2(0.0), vec2(1.0))).rgb);
                float bl = luma(texture(uCurrentTexture, clamp(uv + vec2(-texel.x, -texel.y), vec2(0.0), vec2(1.0))).rgb);
                float bc = luma(texture(uCurrentTexture, clamp(uv + vec2( 0.0,     -texel.y), vec2(0.0), vec2(1.0))).rgb);
                float br = luma(texture(uCurrentTexture, clamp(uv + vec2( texel.x, -texel.y), vec2(0.0), vec2(1.0))).rgb);

                float gx = -tl + tr - 2.0 * ml + 2.0 * mr - bl + br;
                float gy =  tl + 2.0 * tc + tr - bl - 2.0 * bc - br;
                return vec2(gx, gy);
            }

            void main()
            {
                vec3 current = texture(uCurrentTexture, vUv).rgb;

                vec2 texSize = vec2(textureSize(uCurrentTexture, 0));
                vec2 safeTexSize = max(texSize, vec2(1.0));
                vec2 texel = 1.0 / safeTexSize;

                vec2 edgeGrad = sobel(vUv, texel);
                float edgeMagnitude = length(edgeGrad) * max(uEdgeStrength, 0.0);
                float threshold = clamp(uEdgeThreshold, 0.0, 1.0);
                float softness = max(uEdgeSoftness, 0.001);
                float edgeMask = smoothstep(threshold, min(1.0, threshold + softness), edgeMagnitude);

                float dx = lumaDelta(vUv + vec2(texel.x, 0.0)) - lumaDelta(vUv - vec2(texel.x, 0.0));
                float dy = lumaDelta(vUv + vec2(0.0, texel.y)) - lumaDelta(vUv - vec2(0.0, texel.y));
                vec2 flow = vec2(dx, dy) * max(uFlowGain, 0.0);

                vec2 edgeDir = normalize(edgeGrad + vec2(1e-6));
                vec2 flowDir = normalize(flow + vec2(1e-6));
                vec2 bleedDir = normalize(edgeDir + flowDir + vec2(1e-6));

                vec2 offsetUv = bleedDir * (uBleedDistancePixels / safeTexSize);
                vec2 bleedUv = clamp(vUv + offsetUv, vec2(0.0), vec2(1.0));

                vec3 feedbackCenter = texture(uPrevFeedback, vUv).rgb * clamp(uFeedbackDecay, 0.0, 1.0);
                vec3 feedbackBleed = texture(uPrevFeedback, bleedUv).rgb * clamp(uFeedbackDecay, 0.0, 1.0);

                vec3 edgeColor = mix(feedbackCenter, feedbackBleed, edgeMask);
                edgeColor = mix(edgeColor, vec3(edgeMagnitude), clamp(uEdgeTint, 0.0, 1.0) * edgeMask);

                vec3 recirculated = mix(edgeColor, current, clamp(uFreshMix, 0.0, 1.0));
                vec3 color = mix(current, recirculated, clamp(uMix, 0.0, 1.0));

                fragColor = vec4(color, 1.0);
            }
            """;
    }
}
