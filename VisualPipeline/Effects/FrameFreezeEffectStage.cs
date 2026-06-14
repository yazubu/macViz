using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class FrameFreezeEffectStage : PipelineStage
    {
        public const string TypeIdValue = "effect.frameFreeze";

        private readonly Parameter<float> _freezeSignal = new("Frame Freeze / Trigger Signal", 0f, 2f, 0f);
        private readonly Parameter<float> _freezeThreshold = new("Frame Freeze / Signal Threshold", 0f, 2f, 0.8f);
        private readonly Parameter<float> _freezeDuration = new("Frame Freeze / Duration (s)", 0.01f, 8f, 0.35f);
        private readonly Parameter<int> _freezeMode = new("Frame Freeze / Mode (0 Timed, 1 Hold Until Trigger)", 0, 1, 0);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _frozenTexture;
        private int _lastWidth;
        private int _lastHeight;

        private bool _isFrozen;
        private bool _wasAboveThreshold;
        private float _freezeEndTime = -10_000f;

        public FrameFreezeEffectStage()
        {
            _parameters =
            [
                _freezeSignal,
                _freezeThreshold,
                _freezeDuration,
                _freezeMode
            ];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Frame Freeze";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public override void OnResize(int width, int height, VisualPipeline host)
        {
            if (width == _lastWidth && height == _lastHeight)
            {
                return;
            }

            _lastWidth = width;
            _lastHeight = height;

            if (_frozenTexture != 0)
            {
                GL.DeleteTexture(_frozenTexture);
                _frozenTexture = 0;
            }

            _frozenTexture = CreateRenderTexture(width, height);
            _isFrozen = false;
            _wasAboveThreshold = false;
            _freezeEndTime = -10_000f;
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            UpdateFreezeState(host, inputTexture, time);

            var textureToDraw = _isFrozen && _frozenTexture != 0
                ? _frozenTexture
                : inputTexture;

            if (textureToDraw == 0)
            {
                GL.ClearColor(0f, 0f, 0f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                return;
            }

            host.DrawFullscreen(host._blitProgram, textureToDraw);
        }

        private void UpdateFreezeState(VisualPipeline host, int inputTexture, float currentTime)
        {
            var signal = _freezeSignal.CurrentValue;
            var threshold = _freezeThreshold.CurrentValue;
            var isAboveThreshold = signal >= threshold;
            var trigger = isAboveThreshold && !_wasAboveThreshold;

            var mode = _freezeMode.CurrentValue;

            if (trigger && _frozenTexture != 0 && inputTexture != 0)
            {
                if (mode == 1)
                {
                    host.CopyTexture(inputTexture, _frozenTexture);
                    _isFrozen = true;
                }
                else
                {
                    host.CopyTexture(inputTexture, _frozenTexture);
                    _isFrozen = true;
                    _freezeEndTime = currentTime + MathF.Max(0.01f, _freezeDuration.CurrentValue);
                }
            }

            if (mode == 0 && _isFrozen && currentTime >= _freezeEndTime)
            {
                _isFrozen = false;
            }

            _wasAboveThreshold = isAboveThreshold;
        }

        public override void Dispose()
        {
            if (_frozenTexture != 0)
            {
                GL.DeleteTexture(_frozenTexture);
                _frozenTexture = 0;
            }
        }
    }
}
