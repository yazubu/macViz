using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class EdgeRadianceEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.edgeRadiance";

        private readonly Parameter<float> _edgeStrength = new("Edge Radiance / Edge Strength", 0f, 8f, 2.2f);
        private readonly Parameter<float> _edgeThreshold = new("Edge Radiance / Edge Threshold", 0f, 1f, 0.2f);
        private readonly Parameter<float> _edgeSoftness = new("Edge Radiance / Edge Softness", 0.001f, 0.6f, 0.14f);
        private readonly Parameter<int> _emitMode = new("Edge Radiance / Emit Mode (0 Bright,1 Dark,2 Both)", 0, 2, 0);
        private readonly Parameter<float> _lumaThreshold = new("Edge Radiance / Luma Threshold", 0f, 1f, 0.72f);
        private readonly Parameter<int> _outlineSets = new("Edge Radiance / Outline Sets", 1, 16, 4);
        private readonly Parameter<float> _outlineDistancePixels = new("Edge Radiance / Outline Distance (px)", 1f, 96f, 10f);
        private readonly Parameter<float> _setFade = new("Edge Radiance / Set Fade", 0f, 1f, 0.25f);
        private readonly Parameter<float> _outlineGain = new("Edge Radiance / Outline Gain", 0f, 6f, 1.4f);
        private readonly Parameter<float> _blurDistancePixels = new("Edge Radiance / Gaussian Blur Distance (px)", 0f, 32f, 2.5f);
        private readonly Parameter<float> _mix = new("Edge Radiance / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uEdgeStrength;
        private int _uEdgeThreshold;
        private int _uEdgeSoftness;
        private int _uEmitMode;
        private int _uLumaThreshold;
        private int _uOutlineSets;
        private int _uOutlineDistancePixels;
        private int _uSetFade;
        private int _uOutlineGain;
        private int _uBlurDistancePixels;
        private int _uMix;

        public EdgeRadianceEffectStage()
        {
            _parameters =
            [
                _edgeStrength,
                _edgeThreshold,
                _edgeSoftness,
                _emitMode,
                _lumaThreshold,
                _outlineSets,
                _outlineDistancePixels,
                _setFade,
                _outlineGain,
                _blurDistancePixels,
                _mix
            ];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Edge Radiance";
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
                uniform float uEdgeThreshold;
                uniform float uEdgeSoftness;
                uniform int uEmitMode;
                uniform float uLumaThreshold;
                uniform int uOutlineSets;
                uniform float uOutlineDistancePixels;
                uniform float uSetFade;
                uniform float uOutlineGain;
                uniform float uBlurDistancePixels;
                uniform float uMix;

                const float TAU = 6.28318530718;
                const int MAX_OUTLINE_SETS = 16;
                const int DIRECTION_COUNT = 12;

                float luma(vec3 c)
                {
                    return dot(c, vec3(0.299, 0.587, 0.114));
                }

                vec2 gradAt(vec2 uv, vec2 texel)
                {
                    vec2 safeUv = clamp(uv, vec2(0.0), vec2(1.0));
                    float l = luma(texture(uTexture, clamp(safeUv - vec2(texel.x, 0.0), vec2(0.0), vec2(1.0))).rgb);
                    float r = luma(texture(uTexture, clamp(safeUv + vec2(texel.x, 0.0), vec2(0.0), vec2(1.0))).rgb);
                    float d = luma(texture(uTexture, clamp(safeUv - vec2(0.0, texel.y), vec2(0.0), vec2(1.0))).rgb);
                    float u = luma(texture(uTexture, clamp(safeUv + vec2(0.0, texel.y), vec2(0.0), vec2(1.0))).rgb);
                    return vec2(r - l, u - d);
                }

                float edgeMaskFromGrad(vec2 grad)
                {
                    float edge = length(grad) * max(uEdgeStrength, 0.0);
                    float t = clamp(uEdgeThreshold, 0.0, 1.0);
                    float softness = max(uEdgeSoftness, 0.001);
                    return smoothstep(t, min(1.0, t + softness), edge);
                }

                float emitMaskFromLuma(float lum)
                {
                    float threshold = clamp(uLumaThreshold, 0.0, 1.0);
                    int mode = clamp(uEmitMode, 0, 2);

                    if (mode == 0)
                    {
                        return step(threshold, lum);
                    }

                    if (mode == 1)
                    {
                        return step(lum, threshold);
                    }

                    float bright = step(threshold, lum);
                    float dark = step(lum, 1.0 - threshold);
                    return clamp(bright + dark, 0.0, 1.0);
                }

                vec3 gaussianBlurAt(vec2 uv, vec2 safeTexSize)
                {
                    float blurPx = max(uBlurDistancePixels, 0.0);
                    if (blurPx <= 1e-4)
                    {
                        return texture(uTexture, clamp(uv, vec2(0.0), vec2(1.0))).rgb;
                    }

                    vec2 stepUv = vec2(blurPx / safeTexSize.x, blurPx / safeTexSize.y);
                    vec3 accum = vec3(0.0);
                    float weightSum = 0.0;

                    // 3x3 Gaussian kernel:
                    // 1 2 1
                    // 2 4 2
                    // 1 2 1
                    for (int y = -1; y <= 1; y++)
                    {
                        for (int x = -1; x <= 1; x++)
                        {
                            float wx = (x == 0) ? 2.0 : 1.0;
                            float wy = (y == 0) ? 2.0 : 1.0;
                            float w = wx * wy;
                            vec2 offset = vec2(float(x), float(y)) * stepUv;
                            accum += texture(uTexture, clamp(uv + offset, vec2(0.0), vec2(1.0))).rgb * w;
                            weightSum += w;
                        }
                    }

                    return accum / max(weightSum, 1e-5);
                }

                float sourceMaskAt(vec2 uv, vec2 texel)
                {
                    vec2 safeUv = clamp(uv, vec2(0.0), vec2(1.0));
                    vec3 c = texture(uTexture, safeUv).rgb;
                    float emit = emitMaskFromLuma(luma(c));
                    if (emit <= 0.0)
                    {
                        return 0.0;
                    }

                    vec2 g = gradAt(safeUv, texel);
                    return edgeMaskFromGrad(g) * emit;
                }

                void main()
                {
                    vec3 original = texture(uTexture, vUv).rgb;

                    vec2 texSize = vec2(textureSize(uTexture, 0));
                    vec2 safeTexSize = max(texSize, vec2(1.0));
                    vec2 texel = 1.0 / safeTexSize;

                    int outlineSets = clamp(uOutlineSets, 1, MAX_OUTLINE_SETS);
                    float setSpacingPx = max(uOutlineDistancePixels, 0.0);
                    float setFade = clamp(uSetFade, 0.0, 1.0);

                    vec3 outlines = vec3(0.0);

                    for (int setIndex = 1; setIndex <= MAX_OUTLINE_SETS; setIndex++)
                    {
                        if (setIndex > outlineSets)
                        {
                            break;
                        }

                        float distPx = float(setIndex) * setSpacingPx;
                        float thisSetWeight = 1.0 / (1.0 + float(setIndex - 1) * setFade * 4.0);

                        float bestMask = 0.0;
                        vec3 bestColor = vec3(0.0);

                        for (int d = 0; d < DIRECTION_COUNT; d++)
                        {
                            float a = (float(d) / float(DIRECTION_COUNT)) * TAU;
                            vec2 dir = vec2(cos(a), sin(a));
                            vec2 sampleUv = clamp(vUv + dir * (distPx / safeTexSize), vec2(0.0), vec2(1.0));

                            float mask = sourceMaskAt(sampleUv, texel);
                            if (mask > bestMask)
                            {
                                bestMask = mask;
                                bestColor = gaussianBlurAt(sampleUv, safeTexSize);
                            }
                        }

                        outlines += bestColor * bestMask * thisSetWeight;
                    }

                    vec3 effected = original + outlines * max(uOutlineGain, 0.0);
                    vec3 color = mix(original, effected, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uEdgeStrength = GL.GetUniformLocation(_program, "uEdgeStrength");
            _uEdgeThreshold = GL.GetUniformLocation(_program, "uEdgeThreshold");
            _uEdgeSoftness = GL.GetUniformLocation(_program, "uEdgeSoftness");
            _uEmitMode = GL.GetUniformLocation(_program, "uEmitMode");
            _uLumaThreshold = GL.GetUniformLocation(_program, "uLumaThreshold");
            _uOutlineSets = GL.GetUniformLocation(_program, "uOutlineSets");
            _uOutlineDistancePixels = GL.GetUniformLocation(_program, "uOutlineDistancePixels");
            _uSetFade = GL.GetUniformLocation(_program, "uSetFade");
            _uOutlineGain = GL.GetUniformLocation(_program, "uOutlineGain");
            _uBlurDistancePixels = GL.GetUniformLocation(_program, "uBlurDistancePixels");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            GL.UseProgram(_program);
            GL.Uniform1(_uEdgeStrength, _edgeStrength.CurrentValue);
            GL.Uniform1(_uEdgeThreshold, _edgeThreshold.CurrentValue);
            GL.Uniform1(_uEdgeSoftness, _edgeSoftness.CurrentValue);
            GL.Uniform1(_uEmitMode, _emitMode.CurrentValue);
            GL.Uniform1(_uLumaThreshold, _lumaThreshold.CurrentValue);
            GL.Uniform1(_uOutlineSets, _outlineSets.CurrentValue);
            GL.Uniform1(_uOutlineDistancePixels, _outlineDistancePixels.CurrentValue);
            GL.Uniform1(_uSetFade, _setFade.CurrentValue);
            GL.Uniform1(_uOutlineGain, _outlineGain.CurrentValue);
            GL.Uniform1(_uBlurDistancePixels, _blurDistancePixels.CurrentValue);
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
