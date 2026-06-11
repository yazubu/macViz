namespace macViz;

public enum LfoWaveType
{
    Sine,
    Square,
    Sawtooth,
    PWM,
    Random,
    SampleAndHold
}

public sealed class LFO
{
    private readonly Random _random = new();
    private float _phaseAccumulator;
    private float _previousPhase;
    private float _sampleAndHoldValue;

    public int Id { get; }
    public LfoWaveType WaveType { get; set; } = LfoWaveType.Sine;
    public float Frequency { get; set; } = 1.0f;
    public float Phase { get; set; }
    public float DutyCycle { get; set; } = 0.5f;
    public bool SyncEnabled { get; set; }
    public float SyncSpeedMultiplier { get; set; } = 1.0f;
    public float Output { get; private set; }

    public LFO(int id)
    {
        Id = id;
        _sampleAndHoldValue = NextRandomValue();
    }

    public float GetValue(float deltaTime, TempoController tempo)
    {
        float phase;

        if (SyncEnabled)
        {
            var beatProgress = (float)(tempo.BeatNumber + tempo.BeatPhase);
            phase = (beatProgress * SyncSpeedMultiplier + Phase) % 1f;
            if (phase < 0f) phase += 1f;
        }
        else
        {
            _phaseAccumulator = (_phaseAccumulator + (Frequency * deltaTime)) % 1f;
            phase = (_phaseAccumulator + Phase) % 1f;
            if (phase < 0f) phase += 1f;
        }

        Output = WaveType switch
        {
            LfoWaveType.Sine => MathF.Sin(phase * 2f * MathF.PI),
            LfoWaveType.Square => phase < 0.5f ? 1f : -1f,
            LfoWaveType.Sawtooth => (2f * phase) - 1f,
            LfoWaveType.PWM => phase < Math.Clamp(DutyCycle, 0.05f, 0.95f) ? 1f : -1f,
            LfoWaveType.Random => NextRandomValue(),
            LfoWaveType.SampleAndHold => SampleAndHold(phase),
            _ => 0f
        };

        _previousPhase = phase;
        return Output;
    }

    private float SampleAndHold(float currentPhase)
    {
        if (currentPhase < _previousPhase)
        {
            _sampleAndHoldValue = NextRandomValue();
        }

        return _sampleAndHoldValue;
    }

    private float NextRandomValue() => (float)(_random.NextDouble() * 2.0 - 1.0);
}

public sealed class LfoEngine
{
    private int _nextId = 1;

    public List<LFO> Lfos { get; } = [];

    public LFO AddLfo()
    {
        var lfo = new LFO(_nextId++);
        Lfos.Add(lfo);
        return lfo;
    }

    public void RemoveLfo(int id)
    {
        Lfos.RemoveAll(x => x.Id == id);
    }

    public void Update(float deltaTime, TempoController tempo)
    {
        foreach (var lfo in Lfos)
        {
            lfo.GetValue(deltaTime, tempo);
        }
    }

    public bool TryGetOutput(int id, out float value)
    {
        var lfo = Lfos.FirstOrDefault(x => x.Id == id);
        if (lfo is null)
        {
            value = 0f;
            return false;
        }

        value = lfo.Output;
        return true;
    }
}
