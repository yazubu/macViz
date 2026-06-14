namespace macViz;

internal sealed class FftAssignmentDto
{
    public int SourceId { get; set; }
    public int BinIndex { get; set; }
    public float Scale { get; set; } = 1f;
    public float Offset { get; set; }
}
