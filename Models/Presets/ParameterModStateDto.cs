namespace macViz;

internal sealed class ParameterModStateDto
{
    public int ParameterIndex { get; set; }
    public string ParameterName { get; set; } = string.Empty;
    public ModulationInteractionMode InteractionMode { get; set; } = ModulationInteractionMode.Add;
    public List<LfoAssignmentDto> LfoAssignments { get; set; } = [];
    public List<FftAssignmentDto> FftAssignments { get; set; } = [];
}
