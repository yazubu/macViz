namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class SignalSwitchStage : PipelineStage
    {
        public const string TypeIdValue = "effect.signalSwitch";
        public const int MaxInputs = 8;

        private enum SwitchAlgorithm
        {
            Order = 0,
            OrderInv = 1,
            Random = 2,
            RandomEx = 3
        }

        private readonly Parameter<int> _algorithm = new("Signal Switch / Algorithm (0 Order,1 Order Inv,2 Random,3 Random EX)", 0, 3, 0);
        private readonly Parameter<float> _trigger = new("Signal Switch / Trigger", 0f, 1f, 0f);
        private readonly IReadOnlyList<IParameter> _parameters;

        private readonly Random _random = new(1977);
        private int _selectedInputIndex;
        private bool _previousTriggerHigh;

        public SignalSwitchStage()
        {
            _parameters = [_algorithm, _trigger];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Signal Switch";
        public override IReadOnlyList<IParameter> Parameters => _parameters;

        public int SelectInputTexture(IReadOnlyList<int?> inputNodeIds, Func<int?, int> resolveTexture)
        {
            var activeTextures = new List<int>(MaxInputs);
            for (var i = 0; i < inputNodeIds.Count && i < MaxInputs; i++)
            {
                if (!inputNodeIds[i].HasValue)
                {
                    continue;
                }

                var texture = resolveTexture(inputNodeIds[i]);
                if (texture != 0)
                {
                    activeTextures.Add(texture);
                }
            }

            if (activeTextures.Count == 0)
            {
                _selectedInputIndex = 0;
                _previousTriggerHigh = _trigger.CurrentValue >= 0.5f;
                return 0;
            }

            if (_selectedInputIndex >= activeTextures.Count)
            {
                _selectedInputIndex = 0;
            }

            var triggerHigh = _trigger.CurrentValue >= 0.5f;
            if (triggerHigh && !_previousTriggerHigh)
            {
                Advance(activeTextures.Count);
            }

            _previousTriggerHigh = triggerHigh;
            return activeTextures[_selectedInputIndex];
        }

        private void Advance(int activeCount)
        {
            if (activeCount <= 0)
            {
                _selectedInputIndex = 0;
                return;
            }

            var algorithm = (SwitchAlgorithm)Math.Clamp(_algorithm.CurrentValue, 0, 3);
            switch (algorithm)
            {
                case SwitchAlgorithm.Order:
                    _selectedInputIndex = (_selectedInputIndex + 1) % activeCount;
                    break;
                case SwitchAlgorithm.OrderInv:
                    _selectedInputIndex = (_selectedInputIndex - 1 + activeCount) % activeCount;
                    break;
                case SwitchAlgorithm.Random:
                    _selectedInputIndex = _random.Next(activeCount);
                    break;
                case SwitchAlgorithm.RandomEx:
                    if (activeCount <= 1)
                    {
                        _selectedInputIndex = 0;
                        break;
                    }

                    var next = _selectedInputIndex;
                    while (next == _selectedInputIndex)
                    {
                        next = _random.Next(activeCount);
                    }

                    _selectedInputIndex = next;
                    break;
            }
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            // Render is handled by the graph host so the stage can choose among many inputs.
        }
    }
}
