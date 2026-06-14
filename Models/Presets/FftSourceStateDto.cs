namespace macViz;

internal sealed class FftSourceStateDto
{
    public int SourceId { get; set; }
    public int BinCount { get; set; } = 8;
    public float Smoothing { get; set; } = 0.75f;
    public bool LogarithmicGrouping { get; set; }
    public bool ExpandVariability { get; set; }
    public float VariabilityWindowSeconds { get; set; } = 2f;
}
