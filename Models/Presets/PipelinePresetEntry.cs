namespace macViz;

internal sealed class PipelinePresetEntry
{
    public string Name { get; set; } = "Preset";
    public VisualPipelinePresetState Pipeline { get; set; } = new();
    public List<LfoStateDto> Lfos { get; set; } = [];
    public List<FftSourceStateDto> FftSources { get; set; } = [];
    public List<ParameterModStateDto> ParameterModulations { get; set; } = [];
    public List<ModGraphNodePositionDto> ModGraphNodePositions { get; set; } = [];
}
