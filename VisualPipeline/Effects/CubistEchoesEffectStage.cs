using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class CubistEchoesEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.cubistEchoes";
        private const int MaxHistoryFrames = 48;

        private readonly Parameter<int> _gridColumns = new("Cubist Echoes / Grid Columns", 2, 24, 6);
        private readonly Parameter<int> _gridRows = new("Cubist Echoes / Grid Rows", 2, 24, 6);
        private readonly Parameter<int> _historyFrames = new("Cubist Echoes / History Frames", 2, MaxHistoryFrames, 20);
        private readonly Parameter<float> _driftRate = new("Cubist Echoes / Drift Rate", 0f, 8f, 0.35f);
        private readonly Parameter<float> _driftAmount = new("Cubist Echoes / Drift Amount", 0f, 1f, 0.35f);
        private readonly Parameter<float> _randomSeed = new("Cubist Echoes / Random Seed", 0f, 1000f, 17f);
        private readonly Parameter<float> _uniqueDelaySpread = new("Cubist Echoes / Unique Delay Spread", 0f, 1f, 0.9f);
        private readonly Parameter<float> _mix = new("Cubist Echoes / Mix", 0f, 1f, 1f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _historyArrayTexture;
        private int _historyFbo;
        private int _writeIndex;
        private int _latestLayer;
        private bool _hasHistory;
        private int _lastWidth;
        private int _lastHeight;

        private int _program;
        private int _uCurrentTexture;
        private int _uHistoryArray;
        private int _uGrid;
        private int _uHistoryLength;
        private int _uMaxHistory;
        private int _uLatestLayer;
        private int _uDriftRate;
        private int _uDriftAmount;
        private int _uRandomSeed;
        private int _uUniqueDelaySpread;
        private int _uTime;
        private int _uMix;

        public CubistEchoesEffectStage()
        {
            _parameters =
            [
                _gridColumns,
                _gridRows,
                _historyFrames,
                _driftRate,
                _driftAmount,
                _randomSeed,
                _uniqueDelaySpread,
                _mix
            ];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Cubist Echoes";
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

                uniform vec2 uGrid;
                uniform int uHistoryLength;
                uniform int uMaxHistory;
                uniform int uLatestLayer;
                uniform float uDriftRate;
                uniform float uDriftAmount;
                uniform float uRandomSeed;
                uniform float uUniqueDelaySpread;
                uniform float uTime;
                uniform float uMix;

                const float TAU = 6.28318530718;

                float hash12(vec2 p)
                {
                    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
                    p3 += dot(p3, p3.yzx + 33.33);
                    return fract((p3.x + p3.y) * p3.z);
                }

                void main()
                {
                    vec3 current = texture(uCurrentTexture, vUv).rgb;

                    vec2 grid = max(uGrid, vec2(1.0));
                    vec2 cell = floor(clamp(vUv, vec2(0.0), vec2(0.999999)) * grid);

                    int historyLength = max(uHistoryLength, 1);
                    float historyLenF = float(historyLength);

                    int cellX = int(cell.x);
                    int cellY = int(cell.y);
                    int gridX = max(int(grid.x), 1);
                    int linearCellId = cellY * gridX + cellX;

                    float spread = clamp(uUniqueDelaySpread, 0.0, 1.0);
                    float cellRandA = hash12(cell * vec2(13.13, 37.37) + vec2(uRandomSeed + 19.31, uRandomSeed + 3.11));
                    float cellRandB = hash12(cell * vec2(71.17, 29.29) + vec2(uRandomSeed + 73.77, uRandomSeed + 1.27));

                    // Spread=0: mostly random per-tile delays.
                    int randomDelay = int(floor(cellRandA * historyLenF));

                    // Spread=1: strongly stratified distribution across history indices.
                    float strideF = max(1.0, floor((historyLenF - 1.0) * 0.73));
                    int stratifiedDelay = int(mod(floor(float(linearCellId) * strideF) + floor(cellRandB * historyLenF), historyLenF));

                    int staticDelay = int(round(mix(float(randomDelay), float(stratifiedDelay), spread)));

                    // Optional temporal drift: each tile oscillates with a different phase.
                    float phase = cellRandA * TAU + float(linearCellId) * 0.173;
                    float osc = sin(uTime * max(uDriftRate, 0.0) + phase);
                    int driftSpan = max(1, int(floor((historyLenF - 1.0) * 0.5)));
                    int oscillatingDelay = staticDelay + int(round(osc * float(driftSpan) * clamp(uDriftAmount, 0.0, 1.0)));

                    int delayIndex = ((oscillatingDelay % historyLength) + historyLength) % historyLength;
                    int layer = (uLatestLayer - delayIndex + uMaxHistory) % uMaxHistory;

                    vec3 echoes = texture(uHistoryArray, vec3(vUv, float(layer))).rgb;
                    vec3 color = mix(current, echoes, clamp(uMix, 0.0, 1.0));
                    fragColor = vec4(color, 1.0);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uCurrentTexture = GL.GetUniformLocation(_program, "uCurrentTexture");
            _uHistoryArray = GL.GetUniformLocation(_program, "uHistoryArray");
            _uGrid = GL.GetUniformLocation(_program, "uGrid");
            _uHistoryLength = GL.GetUniformLocation(_program, "uHistoryLength");
            _uMaxHistory = GL.GetUniformLocation(_program, "uMaxHistory");
            _uLatestLayer = GL.GetUniformLocation(_program, "uLatestLayer");
            _uDriftRate = GL.GetUniformLocation(_program, "uDriftRate");
            _uDriftAmount = GL.GetUniformLocation(_program, "uDriftAmount");
            _uRandomSeed = GL.GetUniformLocation(_program, "uRandomSeed");
            _uUniqueDelaySpread = GL.GetUniformLocation(_program, "uUniqueDelaySpread");
            _uTime = GL.GetUniformLocation(_program, "uTime");
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

            WriteInputIntoHistoryLayer(host, inputTexture, _writeIndex);
            _latestLayer = _writeIndex;
            _writeIndex = (_writeIndex + 1) % MaxHistoryFrames;

            var historyLength = Math.Clamp(_historyFrames.CurrentValue, 2, MaxHistoryFrames);

            GL.UseProgram(_program);
            GL.Uniform2(_uGrid, _gridColumns.CurrentValue, _gridRows.CurrentValue);
            GL.Uniform1(_uHistoryLength, historyLength);
            GL.Uniform1(_uMaxHistory, MaxHistoryFrames);
            GL.Uniform1(_uLatestLayer, _latestLayer);
            GL.Uniform1(_uDriftRate, _driftRate.CurrentValue);
            GL.Uniform1(_uDriftAmount, _driftAmount.CurrentValue);
            GL.Uniform1(_uRandomSeed, _randomSeed.CurrentValue);
            GL.Uniform1(_uUniqueDelaySpread, _uniqueDelaySpread.CurrentValue);
            GL.Uniform1(_uTime, time);
            GL.Uniform1(_uMix, _mix.CurrentValue);

            host.DrawFullscreenWithTextureBindings(
                _program,
                (0, TextureTarget.Texture2D, inputTexture),
                (1, TextureTarget.Texture2DArray, _historyArrayTexture));
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
                throw new InvalidOperationException($"CubistEchoes framebuffer incomplete: {status}");
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
