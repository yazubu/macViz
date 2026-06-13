using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class SnapshotPeakEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.snapshotPeak";
        private readonly Parameter<float> _snapshotSignal = new("Snapshot Effect / Signal", 0f, 2f, 0f);
        private readonly Parameter<float> _peakThreshold = new("Snapshot Effect / Peak Threshold", 0f, 2f, 0.8f);
        private readonly Parameter<float> _minPeakInterval = new("Snapshot Effect / Min Interval (s)", 0.02f, 2f, 0.2f);
        private readonly Parameter<float> _holdSeconds = new("Snapshot Effect / Hold Time (s)", 0.05f, 8f, 2.5f);
        private readonly Parameter<int> _snapshotCount = new("Snapshot Effect / Count", 1, MaxSnapshots, MaxSnapshots);
        private readonly Parameter<float> _snapshotOpacity = new("Snapshot Effect / Opacity", 0f, 1f, 1f);
        private readonly Parameter<float> _opacityDrop = new("Snapshot Effect / Opacity Drop", 0f, 0.95f, 0.2f);
        private readonly Parameter<float> _mix = new("Snapshot Effect / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private readonly int[] _snapshotTextures = new int[MaxSnapshots];
        private readonly float[] _snapshotTimes = new float[MaxSnapshots];
        private readonly float[] _snapshotWeights = new float[MaxSnapshots];

        private int _writeIndex;
        private bool _signalInitialized;
        private float _signalPrev2;
        private float _signalPrev1;
        private float _lastCaptureTime = -10_000f;
        private int _lastWidth;
        private int _lastHeight;

        private int _program;
        private readonly int[] _uSnapshotSamplers = new int[MaxSnapshots];
        private int _uInput;
        private int _uWeights;
        private int _uMix;

        public SnapshotPeakEffectStage()
        {
            _parameters =
            [
                _snapshotSignal,
                _peakThreshold,
                _minPeakInterval,
                _holdSeconds,
                _snapshotCount,
                _snapshotOpacity,
                _opacityDrop,
                _mix
            ];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Snapshot Peak Hold";
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

                uniform sampler2D uInputTexture;
                uniform sampler2D uSnapshot0;
                uniform sampler2D uSnapshot1;
                uniform sampler2D uSnapshot2;
                uniform sampler2D uSnapshot3;
                uniform sampler2D uSnapshot4;
                uniform sampler2D uSnapshot5;
                uniform sampler2D uSnapshot6;
                uniform sampler2D uSnapshot7;
                uniform float uSnapshotWeights[8];
                uniform float uMix;

                vec3 snapshotAt(int index, vec2 uv)
                {
                    if (index == 0) return texture(uSnapshot0, uv).rgb;
                    if (index == 1) return texture(uSnapshot1, uv).rgb;
                    if (index == 2) return texture(uSnapshot2, uv).rgb;
                    if (index == 3) return texture(uSnapshot3, uv).rgb;
                    if (index == 4) return texture(uSnapshot4, uv).rgb;
                    if (index == 5) return texture(uSnapshot5, uv).rgb;
                    if (index == 6) return texture(uSnapshot6, uv).rgb;
                    return texture(uSnapshot7, uv).rgb;
                }

                void main()
                {
                    vec3 live = texture(uInputTexture, vUv).rgb;
                    vec3 composited = live;

                    for (int i = 0; i < 8; i++)
                    {
                        float w = clamp(uSnapshotWeights[i], 0.0, 1.0);
                        if (w <= 0.0001)
                        {
                            continue;
                        }

                        vec3 shot = snapshotAt(i, vUv);
                        composited = mix(composited, shot, w);
                    }

                    vec3 color = mix(live, composited, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uInput = GL.GetUniformLocation(_program, "uInputTexture");
            _uWeights = GL.GetUniformLocation(_program, "uSnapshotWeights");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            for (var i = 0; i < MaxSnapshots; i++)
            {
                _uSnapshotSamplers[i] = GL.GetUniformLocation(_program, $"uSnapshot{i}");
            }

            GL.UseProgram(_program);
            GL.Uniform1(_uInput, 0);
            for (var i = 0; i < MaxSnapshots; i++)
            {
                GL.Uniform1(_uSnapshotSamplers[i], i + 1);
            }
            GL.UseProgram(0);
        }

        public override void OnResize(int width, int height, VisualPipeline host)
        {
            if (width == _lastWidth && height == _lastHeight)
            {
                return;
            }

            _lastWidth = width;
            _lastHeight = height;

            for (var i = 0; i < MaxSnapshots; i++)
            {
                if (_snapshotTextures[i] != 0)
                {
                    GL.DeleteTexture(_snapshotTextures[i]);
                    _snapshotTextures[i] = 0;
                }

                _snapshotTextures[i] = CreateRenderTexture(width, height);
                _snapshotTimes[i] = -10_000f;
                _snapshotWeights[i] = 0f;
            }

            _writeIndex = 0;
            _signalInitialized = false;
            _lastCaptureTime = -10_000f;
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            UpdatePeakCapture(host, inputTexture, time);

            var hold = MathF.Max(0.01f, _holdSeconds.CurrentValue);
            var activeCount = Math.Clamp(_snapshotCount.CurrentValue, 1, MaxSnapshots);
            var opacity = Math.Clamp(_snapshotOpacity.CurrentValue, 0f, 1f);
            var drop = Math.Clamp(_opacityDrop.CurrentValue, 0f, 0.95f);

            for (var i = 0; i < MaxSnapshots; i++)
            {
                var sourceIndex = (_writeIndex - 1 - i + MaxSnapshots) % MaxSnapshots;
                var age = time - _snapshotTimes[sourceIndex];
                var holdWeight = age >= 0f && age <= hold ? 1f - (age / hold) : 0f;
                var rankWeight = MathF.Pow(1f - drop, i);
                _snapshotWeights[i] = i < activeCount ? holdWeight * rankWeight * opacity : 0f;
            }

            GL.UseProgram(_program);
            GL.Uniform1(_uWeights, MaxSnapshots, _snapshotWeights);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            var bindings = new (int TextureUnitIndex, int TextureId)[1 + MaxSnapshots];
            bindings[0] = (0, inputTexture);
            for (var i = 0; i < MaxSnapshots; i++)
            {
                var sourceIndex = (_writeIndex - 1 - i + MaxSnapshots) % MaxSnapshots;
                bindings[i + 1] = (1 + i, _snapshotTextures[sourceIndex]);
            }

            host.DrawFullscreenWithTextures(_program, bindings);
        }

        private void UpdatePeakCapture(VisualPipeline host, int inputTexture, float currentTime)
        {
            var signal = _snapshotSignal.CurrentValue;
            if (!_signalInitialized)
            {
                _signalPrev2 = signal;
                _signalPrev1 = signal;
                _signalInitialized = true;
                return;
            }

            var threshold = _peakThreshold.CurrentValue;
            var minInterval = MathF.Max(0.01f, _minPeakInterval.CurrentValue);

            var isLocalPeak = _signalPrev1 > _signalPrev2 && _signalPrev1 >= signal;
            var passesThreshold = _signalPrev1 >= threshold;
            var canCapture = currentTime - _lastCaptureTime >= minInterval;

            if (isLocalPeak && passesThreshold && canCapture)
            {
                host.CopyTexture(inputTexture, _snapshotTextures[_writeIndex]);
                _snapshotTimes[_writeIndex] = currentTime;
                _writeIndex = (_writeIndex + 1) % MaxSnapshots;
                _lastCaptureTime = currentTime;
            }

            _signalPrev2 = _signalPrev1;
            _signalPrev1 = signal;
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }

            for (var i = 0; i < MaxSnapshots; i++)
            {
                if (_snapshotTextures[i] != 0)
                {
                    GL.DeleteTexture(_snapshotTextures[i]);
                    _snapshotTextures[i] = 0;
                }
            }
        }
    }
}
