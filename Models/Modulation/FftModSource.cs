namespace macViz;

internal sealed class FftModSource
{
    public int Id;
    public int BinCount = 8;
    public float Smoothing = 0.75f;
    public bool LogarithmicGrouping;
    public bool ExpandVariability;
    public float VariabilityWindowSeconds = 2f;
    public float[] SmoothedBins = new float[8];
    public Queue<(double Time, float[] Bins)> VariabilityHistory = new();
}
