using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class CubistDelayEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.cubistDelay";
        private const int MaxHistoryFrames = 64;

        private readonly Parameter<int> _gridColumns = new("Cubist Delay / Grid Columns", 1, 64, 8);
        private readonly Parameter<int> _gridRows = new("Cubist Delay / Grid Rows", 1, 64, 8);
        private readonly Parameter<float> _maxDelaySeconds = new("Cubist Delay / Max Delay (s)", 0f, 2f, 2f);
        private readonly Parameter<float> _randomSeed = new("Cubist Delay / Random Seed", 0f, 1000f, 37f);
        private readonly Parameter<float> _mix = new("Cubist Delay / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _historyArrayTexture;
        private int _historyFbo;
        private int _writeIndex;
        private int _latestLayer;
        private bool _hasHistory;
        private int _lastWidth;
        private int _lastHeight;

        private double _lastRenderTime = double.NaN;
        private float _fpsEstimate = 30f;
        private int _renderFrameCounter;

        private int _program;
        private int _uCurrentTexture;
        private int _uHistoryArray;
        private int _uGridColumns;
        private int _uGridRows;
        private int _uHistoryLength;
        private int _uMaxHistory;
        private int _uLatestLayer;
        private int _uMaxDelaySeconds;
        private int _uRandomSeed;
        private int _uFramesPerSecond;
        private int _uMix;

        public CubistDelayEffectStage()
        {
            _parameters =
            [
                _gridColumns,
                _gridRows,
                _maxDelaySeconds,
                _randomSeed,
                _mix
            ];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Cubist Delay";
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

                uniform sampler2D uCurrentTexture;
                uniform sampler2DArray uHistoryArray;

                uniform int uGridColumns;
                uniform int uGridRows;
                uniform int uHistoryLength;
                uniform int uMaxHistory;
                uniform int uLatestLayer;
                uniform float uMaxDelaySeconds;
                uniform float uRandomSeed;
                uniform float uFramesPerSecond;
                uniform float uMix;

                float hash12(vec2 p)
                {
                    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
                    p3 += dot(p3, p3.yzx + 33.33);
                    return fract((p3.x + p3.y) * p3.z);
                }

                void main()
                {
                    vec3 current = texture(uCurrentTexture, vUv).rgb;

                    int gridX = max(uGridColumns, 1);
                    int gridY = max(uGridRows, 1);
                    vec2 gridF = vec2(float(gridX), float(gridY));
                    vec2 cell = floor(clamp(vUv, vec2(0.0), vec2(0.999999)) * gridF);

                    float rnd = hash12(cell + vec2(uRandomSeed * 0.173, uRandomSeed * 0.619));
                    float delaySeconds = rnd * max(uMaxDelaySeconds, 0.0);

                    int historyLength = max(uHistoryLength, 1);
                    float fps = max(uFramesPerSecond, 1.0);
                    int delayFrames = int(round(delaySeconds * fps));
                    delayFrames = clamp(delayFrames, 0, historyLength - 1);

                    int layer = (uLatestLayer - delayFrames + uMaxHistory) % uMaxHistory;
                    vec3 delayed = texture(uHistoryArray, vec3(vUv, float(layer))).rgb;

                    vec3 color = mix(current, delayed, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uCurrentTexture = GL.GetUniformLocation(_program, "uCurrentTexture");
            _uHistoryArray = GL.GetUniformLocation(_program, "uHistoryArray");
            _uGridColumns = GL.GetUniformLocation(_program, "uGridColumns");
            _uGridRows = GL.GetUniformLocation(_program, "uGridRows");
            _uHistoryLength = GL.GetUniformLocation(_program, "uHistoryLength");
            _uMaxHistory = GL.GetUniformLocation(_program, "uMaxHistory");
            _uLatestLayer = GL.GetUniformLocation(_program, "uLatestLayer");
            _uMaxDelaySeconds = GL.GetUniformLocation(_program, "uMaxDelaySeconds");
            _uRandomSeed = GL.GetUniformLocation(_program, "uRandomSeed");
            _uFramesPerSecond = GL.GetUniformLocation(_program, "uFramesPerSecond");
            _uMix = GL.GetUniformLocation(_program, "uMix");

            GL.UseProgram(_program);
            GL.Uniform1(_uCurrentTexture, 0);
            GL.Uniform1(_uHistoryArray, 1);
            GL.UseProgram(0);

            _historyArrayTexture = GL.GenTexture();
            _historyFbo = GL.GenFramebuffer();
        }

        public override void OnResize(int width, int height, VisualPipeline host)
        {
            EnsureResources(host);

            if (_historyArrayTexture == 0)
            {
                return;
            }

            if (width == _lastWidth && height == _lastHeight)
            {
                return;
            }

            _lastWidth = width;
            _lastHeight = height;

            GL.BindTexture(TextureTarget.Texture2DArray, _historyArrayTexture);
            GL.TexImage3D(
                TextureTarget.Texture2DArray,
                0,
                PixelInternalFormat.Rgba,
                width,
                height,
                MaxHistoryFrames,
                0,
                OpenTK.Graphics.OpenGL4.PixelFormat.Rgba,
                PixelType.UnsignedByte,
                IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);

            _writeIndex = 0;
            _latestLayer = 0;
            _hasHistory = false;
            _lastRenderTime = double.NaN;
            _fpsEstimate = 30f;
            _renderFrameCounter = 0;
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            if (_lastWidth <= 0 || _lastHeight <= 0)
            {
                return;
            }

            if (inputTexture == 0)
            {
                GL.ClearColor(0f, 0f, 0f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                return;
            }

            UpdateFpsEstimate(time);

            if (!_hasHistory)
            {
                for (var i = 0; i < MaxHistoryFrames; i++)
                {
                    WriteInputIntoHistoryLayer(host, inputTexture, i);
                }

                _writeIndex = 0;
                _latestLayer = 0;
                _hasHistory = true;
            }

            var maxDelay = Math.Clamp(_maxDelaySeconds.CurrentValue, 0f, 2f);

            // Keep memory fixed while still supporting up to 2s delay at higher FPS
            // by writing into history at a dynamic stride (temporal decimation).
            var fps = Math.Max(1f, _fpsEstimate);
            var requiredFrames = Math.Max(1f, maxDelay * fps);
            var captureStride = Math.Max(1, (int)MathF.Ceiling(requiredFrames / Math.Max(1, MaxHistoryFrames - 1)));

            if ((_renderFrameCounter % captureStride) == 0)
            {
                WriteInputIntoHistoryLayer(host, inputTexture, _writeIndex);
                _latestLayer = _writeIndex;
                _writeIndex = (_writeIndex + 1) % MaxHistoryFrames;
            }

            _renderFrameCounter++;

            var effectiveHistoryFps = fps / captureStride;
            var neededFrames = Math.Max(1, (int)MathF.Ceiling(maxDelay * effectiveHistoryFps) + 1);
            var historyLength = Math.Clamp(neededFrames, 1, MaxHistoryFrames);

            GL.UseProgram(_program);
            GL.Uniform1(_uGridColumns, _gridColumns.CurrentValue);
            GL.Uniform1(_uGridRows, _gridRows.CurrentValue);
            GL.Uniform1(_uHistoryLength, historyLength);
            GL.Uniform1(_uMaxHistory, MaxHistoryFrames);
            GL.Uniform1(_uLatestLayer, _latestLayer);
            GL.Uniform1(_uMaxDelaySeconds, maxDelay);
            GL.Uniform1(_uRandomSeed, _randomSeed.CurrentValue);
            GL.Uniform1(_uFramesPerSecond, effectiveHistoryFps);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            host.DrawFullscreenWithTextureBindings(
                _program,
                (0, TextureTarget.Texture2D, inputTexture),
                (1, TextureTarget.Texture2DArray, _historyArrayTexture));
        }

        private void UpdateFpsEstimate(float time)
        {
            if (double.IsNaN(_lastRenderTime))
            {
                _lastRenderTime = time;
                return;
            }

            var dt = Math.Max(1e-4, time - _lastRenderTime);
            _lastRenderTime = time;

            var fpsInstant = (float)(1.0 / dt);
            fpsInstant = Math.Clamp(fpsInstant, 1f, 240f);
            const float smoothing = 0.12f;
            _fpsEstimate = _fpsEstimate + ((fpsInstant - _fpsEstimate) * smoothing);
        }

        private void WriteInputIntoHistoryLayer(VisualPipeline host, int inputTexture, int layer)
        {
            GL.GetInteger(GetPName.DrawFramebufferBinding, out var previousFramebuffer);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _historyFbo);
            GL.FramebufferTextureLayer(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                _historyArrayTexture,
                0,
                layer);

            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                throw new InvalidOperationException($"CubistDelay framebuffer incomplete: {status}");
            }

            GL.Viewport(0, 0, _lastWidth, _lastHeight);
            host.DrawFullscreen(host._blitProgram, inputTexture);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, previousFramebuffer);
            GL.Viewport(0, 0, _lastWidth, _lastHeight);
        }

        public override void Dispose()
        {
            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }

            if (_historyFbo != 0)
            {
                GL.DeleteFramebuffer(_historyFbo);
                _historyFbo = 0;
            }

            if (_historyArrayTexture != 0)
            {
                GL.DeleteTexture(_historyArrayTexture);
                _historyArrayTexture = 0;
            }
        }
    }
}
