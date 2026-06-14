namespace macViz;

internal sealed class ModGraphProcessorNodeDto
{
    public int ProcessorId { get; set; }
    public string Kind { get; set; } = "Add";
    public float X { get; set; }
    public float Y { get; set; }
    public float ConstantValue { get; set; } = 1f;
    public float NormalizeOutputMin { get; set; }
    public float NormalizeOutputMax { get; set; } = 1f;
    public List<ModGraphSocketLinkDto> Inputs { get; set; } = [];
}
