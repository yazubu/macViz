namespace macViz;

internal sealed class ModGraphTargetLinkDto
{
    public string NodeKey { get; set; } = string.Empty;
    public int ParameterIndex { get; set; } = -1;
    public string ParameterName { get; set; } = string.Empty;
    public ModGraphSocketLinkDto Link { get; set; } = new();
}
