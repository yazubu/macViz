namespace macViz;

internal sealed class ModGraphSocketLinkDto
{
    public string SourceKind { get; set; } = string.Empty;
    public int SourceId { get; set; }
    public int FftBinIndex { get; set; }
}
