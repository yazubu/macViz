namespace macViz;

public interface IBeatClock
{
    float Bpm { get; }
    long BeatNumber { get; }
    float BeatPhase { get; }
    void Update(float deltaTime);
}

public sealed class TempoController : IBeatClock
{
    private double _totalBeats;

    public float Bpm { get; set; } = 120f;

    public long BeatNumber => (long)Math.Floor(_totalBeats);
    public float BeatPhase => (float)(_totalBeats - Math.Floor(_totalBeats));

    public void Update(float deltaTime)
    {
        var beatsPerSecond = Bpm / 60f;
        _totalBeats += beatsPerSecond * deltaTime;
    }
}
