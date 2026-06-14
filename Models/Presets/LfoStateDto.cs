namespace macViz;

internal sealed class LfoStateDto
{
    public int SourceId { get; set; }
    public LfoWaveType WaveType { get; set; } = LfoWaveType.Sine;
    public float Frequency { get; set; } = 1f;
    public float Phase { get; set; }
    public float DutyCycle { get; set; } = 0.5f;
    public bool SyncEnabled { get; set; }
    public float SyncSpeedMultiplier { get; set; } = 1f;
}
