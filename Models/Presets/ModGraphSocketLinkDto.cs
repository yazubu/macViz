namespace macViz;

internal sealed class ModGraphSocketLinkDto
{
    public string SourceKind { get; set; } = string.Empty;
    public int SourceId { get; set; }
    public float LfoScale { get; set; } = 1f;
    public float LfoOffset { get; set; }
    public int FftBinIndex { get; set; }
    public float FftScale { get; set; } = 1f;
    public float FftOffset { get; set; }
}
