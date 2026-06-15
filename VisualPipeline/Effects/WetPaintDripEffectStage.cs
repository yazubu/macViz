using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class WetPaintDripEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.wetPaintDrip";

        private readonly Parameter<float> _edgeStrength = new("Wet Paint Drip / Edge Strength", 0f, 6f, 2.0f);
        private readonly Parameter<float> _edgeThreshold = new("Wet Paint Drip / Edge Threshold", 0f, 1f, 0.2f);
        private readonly Parameter<float> _edgeSoftness = new("Wet Paint Drip / Edge Softness", 0.001f, 0.5f, 0.12f);
        private readonly Parameter<float> _dripLengthPixels = new("Wet Paint Drip / Drip Length (px)", 0f, 320f, 120f);
        private readonly Parameter<float> _dripSpeed = new("Wet Paint Drip / Drip Speed", 0f, 4f, 1.1f);
        private readonly Parameter<float> _dripDensity = new("Wet Paint Drip / Drip Density", 0f, 1f, 0.65f);
        private readonly Parameter<float> _wigglePixels = new("Wet Paint Drip / Wiggle (px)", 0f, 40f, 6f);
        private readonly Parameter<float> _dripDirectionDegrees = new("Wet Paint Drip / Direction (deg)", -180f, 180f, -90f);
        private readonly Parameter<float> _timelineT = new("Wet Paint Drip / Timeline T", -120f, 120f, 0f);
        private readonly Parameter<float> _timeInfluence = new("Wet Paint Drip / Time Influence", -4f, 4f, 1f);
        private readonly Parameter<float> _areaCellPixels = new("Wet Paint Drip / Area Cell (px)", 8f, 220f, 64f);
        private readonly Parameter<float> _areaColorTolerance = new("Wet Paint Drip / Area Color Tolerance", 0.01f, 0.8f, 0.18f);
        private readonly Parameter<float> _areaSaturationGate = new("Wet Paint Drip / Area Saturation Gate", 0f, 1f, 0.15f);
        private readonly Parameter<float> _pathsPerArea = new("Wet Paint Drip / Paths Per Area", 1f, 4f, 2f);
        private readonly Parameter<float> _pathSpreadPixels = new("Wet Paint Drip / Path Spread (px)", 0f, 60f, 14f);
        private readonly Parameter<float> _overwriteStrength = new("Wet Paint Drip / Overwrite Strength", 0f, 1f, 0.9f);
        private readonly Parameter<float> _mix = new("Wet Paint Drip / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;
        private int _uEdgeStrength;
        private int _uEdgeThreshold;
        private int _uEdgeSoftness;
        private int _uDripLengthPixels;
        private int _uDripSpeed;
        private int _uDripDensity;
        private int _uWigglePixels;
        private int _uDripDirectionDegrees;
        private int _uTimelineT;
        private int _uTimeInfluence;
        private int _uAreaCellPixels;
        private int _uAreaColorTolerance;
        private int _uAreaSaturationGate;
        private int _uPathsPerArea;
        private int _uPathSpreadPixels;
        private int _uOverwriteStrength;
        private int _uTime;
        private int _uMix;

        public WetPaintDripEffectStage()
        {
            _parameters =
            [
                _edgeStrength,
                _edgeThreshold,
                _edgeSoftness,
                _dripLengthPixels,
                _dripSpeed,
                _dripDensity,
                _wigglePixels,
                _dripDirectionDegrees,
                _timelineT,
                _timeInfluence,
                _areaCellPixels,
                _areaColorTolerance,
                _areaSaturationGate,
                _pathsPerArea,
                _pathSpreadPixels,
                _overwriteStrength,
                _mix
            ];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Wet Paint Drip";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            _program = CompileProgram(VertexShaderSource, FragmentShaderSource);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");
            _uEdgeStrength = GL.GetUniformLocation(_program, "uEdgeStrength");
            _uEdgeThreshold = GL.GetUniformLocation(_program, "uEdgeThreshold");
            _uEdgeSoftness = GL.GetUniformLocation(_program, "uEdgeSoftness");
            _uDripLengthPixels = GL.GetUniformLocation(_program, "uDripLengthPixels");
            _uDripSpeed = GL.GetUniformLocation(_program, "uDripSpeed");
            _uDripDensity = GL.GetUniformLocation(_program, "uDripDensity");
            _uWigglePixels = GL.GetUniformLocation(_program, "uWigglePixels");
            _uDripDirectionDegrees = GL.GetUniformLocation(_program, "uDripDirectionDegrees");
            _uTimelineT = GL.GetUniformLocation(_program, "uTimelineT");
            _uTimeInfluence = GL.GetUniformLocation(_program, "uTimeInfluence");
            _uAreaCellPixels = GL.GetUniformLocation(_program, "uAreaCellPixels");
            _uAreaColorTolerance = GL.GetUniformLocation(_program, "uAreaColorTolerance");
            _uAreaSaturationGate = GL.GetUniformLocation(_program, "uAreaSaturationGate");
            _uPathsPerArea = GL.GetUniformLocation(_program, "uPathsPerArea");
            _uPathSpreadPixels = GL.GetUniformLocation(_program, "uPathSpreadPixels");
            _uOverwriteStrength = GL.GetUniformLocation(_program, "uOverwriteStrength");
            _uTime = GL.GetUniformLocation(_program, "uTime");
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
            GL.Uniform1(_uDripLengthPixels, _dripLengthPixels.CurrentValue);
            GL.Uniform1(_uDripSpeed, _dripSpeed.CurrentValue);
            GL.Uniform1(_uDripDensity, _dripDensity.CurrentValue);
            GL.Uniform1(_uWigglePixels, _wigglePixels.CurrentValue);
            GL.Uniform1(_uDripDirectionDegrees, _dripDirectionDegrees.CurrentValue);
            GL.Uniform1(_uTimelineT, _timelineT.CurrentValue);
            GL.Uniform1(_uTimeInfluence, _timeInfluence.CurrentValue);
            GL.Uniform1(_uAreaCellPixels, _areaCellPixels.CurrentValue);
            GL.Uniform1(_uAreaColorTolerance, _areaColorTolerance.CurrentValue);
            GL.Uniform1(_uAreaSaturationGate, _areaSaturationGate.CurrentValue);
            GL.Uniform1(_uPathsPerArea, _pathsPerArea.CurrentValue);
            GL.Uniform1(_uPathSpreadPixels, _pathSpreadPixels.CurrentValue);
            GL.Uniform1(_uOverwriteStrength, _overwriteStrength.CurrentValue);
            GL.Uniform1(_uTime, time);
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

            uniform sampler2D uTexture;
            uniform float uEdgeStrength;
            uniform float uEdgeThreshold;
            uniform float uEdgeSoftness;
            uniform float uDripLengthPixels;
            uniform float uDripSpeed;
            uniform float uDripDensity;
            uniform float uWigglePixels;
            uniform float uDripDirectionDegrees;
            uniform float uTimelineT;
            uniform float uTimeInfluence;
            uniform float uAreaCellPixels;
            uniform float uAreaColorTolerance;
            uniform float uAreaSaturationGate;
            uniform float uPathsPerArea;
            uniform float uPathSpreadPixels;
            uniform float uOverwriteStrength;
            uniform float uTime;
            uniform float uMix;

            const int DRIP_SAMPLES = 14;
            const int MAX_PATHS = 4;
            const float TAU = 6.28318530718;

            float luma(vec3 c)
            {
                return dot(c, vec3(0.299, 0.587, 0.114));
            }

            float hash12(vec2 p)
            {
                vec3 p3 = fract(vec3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return fract((p3.x + p3.y) * p3.z);
            }

            vec3 sampleColor(vec2 uv)
            {
                return texture(uTexture, clamp(uv, vec2(0.0), vec2(1.0))).rgb;
            }

            float edgeMaskAt(vec2 uv, vec2 texel)
            {
                vec2 cUv = clamp(uv, vec2(0.0), vec2(1.0));
                float l = luma(sampleColor(cUv - vec2(texel.x, 0.0)));
                float r = luma(sampleColor(cUv + vec2(texel.x, 0.0)));
                float u = luma(sampleColor(cUv + vec2(0.0, texel.y)));
                float d = luma(sampleColor(cUv - vec2(0.0, texel.y)));

                float grad = length(vec2(r - l, u - d)) * max(uEdgeStrength, 0.0);
                float threshold = clamp(uEdgeThreshold, 0.0, 1.0);
                float softness = max(uEdgeSoftness, 0.001);
                return smoothstep(threshold, min(1.0, threshold + softness), grad);
            }

            float areaMask(vec2 centerUv, vec2 safeTexSize)
            {
                float cellPx = max(uAreaCellPixels, 2.0);
                float tol = max(uAreaColorTolerance, 0.001);

                vec2 d = vec2((cellPx * 0.32) / safeTexSize.x, (cellPx * 0.32) / safeTexSize.y);
                vec3 c = sampleColor(centerUv);
                vec3 x1 = sampleColor(centerUv + vec2(d.x, 0.0));
                vec3 x2 = sampleColor(centerUv - vec2(d.x, 0.0));
                vec3 y1 = sampleColor(centerUv + vec2(0.0, d.y));
                vec3 y2 = sampleColor(centerUv - vec2(0.0, d.y));

                float avgDist = (
                    length(c - x1) +
                    length(c - x2) +
                    length(c - y1) +
                    length(c - y2)) * 0.25;

                float coherent = 1.0 - smoothstep(tol, tol * 2.2 + 1e-5, avgDist);

                float maxC = max(c.r, max(c.g, c.b));
                float minC = min(c.r, min(c.g, c.b));
                float sat = (maxC - minC) / max(maxC, 1e-5);
                float satGate = smoothstep(max(0.0, uAreaSaturationGate * 0.45), max(0.02, uAreaSaturationGate), sat);

                float edgeBoost = edgeMaskAt(centerUv, 1.0 / safeTexSize);
                return coherent * satGate * mix(1.0, edgeBoost, 0.35);
            }

            void main()
            {
                vec3 current = sampleColor(vUv);

                vec2 texSize = vec2(textureSize(uTexture, 0));
                vec2 safeTexSize = max(texSize, vec2(1.0));

                float simTime = uTimelineT - (uTime * uTimeInfluence);
                float dripLengthPx = max(uDripLengthPixels, 0.0);
                float wigglePx = max(uWigglePixels, 0.0);
                float speed = max(uDripSpeed, 0.0);
                float density = clamp(uDripDensity, 0.0, 1.0);
                float pathsPerArea = clamp(uPathsPerArea, 1.0, float(MAX_PATHS));
                float pathSpreadPx = max(uPathSpreadPixels, 0.0);

                float angleRad = radians(uDripDirectionDegrees);
                vec2 flowDirPx = vec2(cos(angleRad), sin(angleRad));
                if (length(flowDirPx) < 1e-5)
                {
                    flowDirPx = vec2(0.0, -1.0);
                }
                flowDirPx = normalize(flowDirPx);
                vec2 orthoDirPx = vec2(-flowDirPx.y, flowDirPx.x);

                vec3 dripAccum = vec3(0.0);
                float dripWeight = 0.0;

                for (int i = 0; i < DRIP_SAMPLES; i++)
                {
                    float f = float(i) / float(DRIP_SAMPLES - 1);
                    float distPx = f * dripLengthPx;

                    vec2 baseUv = vUv - vec2(
                        (flowDirPx.x * distPx) / safeTexSize.x,
                        (flowDirPx.y * distPx) / safeTexSize.y);

                    float cellPx = max(uAreaCellPixels, 2.0);
                    vec2 cell = floor((baseUv * safeTexSize) / cellPx);
                    vec2 cellCenterUv = ((cell + vec2(0.5)) * cellPx) / safeTexSize;
                    vec3 areaColor = sampleColor(cellCenterUv);
                    float area = areaMask(cellCenterUv, safeTexSize);

                    for (int p = 0; p < MAX_PATHS; p++)
                    {
                        float pf = float(p);
                        float laneEnable = step(pf + 0.5, pathsPerArea + 1e-5);
                        if (laneEnable < 0.5)
                        {
                            continue;
                        }

                        float laneSeed = hash12(cell + vec2(pf * 1.73, 23.17));
                        float spawn = step(laneSeed, density * area);

                        float laneCenter = pf - 0.5 * (pathsPerArea - 1.0);
                        float wiggle = sin((simTime * speed * 1.7) + laneSeed * TAU + distPx * 0.08) * wigglePx;
                        float laneOffsetPx = laneCenter * pathSpreadPx + wiggle;

                        vec2 laneUv = baseUv + vec2(
                            (orthoDirPx.x * laneOffsetPx) / safeTexSize.x,
                            (orthoDirPx.y * laneOffsetPx) / safeTexSize.y);

                        float head = fract(simTime * speed * 0.12 + laneSeed * 1.3);
                        float activeLenPx = mix(dripLengthPx * 0.2, dripLengthPx, head);
                        float body = 1.0 - smoothstep(activeLenPx, activeLenPx + max(8.0, dripLengthPx * 0.35), distPx);
                        float tail = exp(-distPx * 0.03);
                        float pixelDistanceToLane = distance(vUv, laneUv) * max(safeTexSize.x, safeTexSize.y);
                        float lanePresence = 1.0 - smoothstep(0.0, 1.2, pixelDistanceToLane);

                        float w = spawn * body * tail * lanePresence;
                        dripAccum += areaColor * w;
                        dripWeight += w;
                    }
                }

                vec3 dripColor = dripWeight > 1e-5 ? (dripAccum / dripWeight) : current;
                float overwrite = clamp(uOverwriteStrength, 0.0, 1.0);
                float dripAlpha = clamp(dripWeight * overwrite, 0.0, 1.0);
                vec3 overwritten = mix(current, dripColor, dripAlpha);
                vec3 color = mix(current, overwritten, clamp(uMix, 0.0, 1.0));

                fragColor = vec4(color, 1.0);
            }
            """;
    }
}
