namespace macViz;

public sealed class VisualPipelinePresetState
{
    public List<VisualPipelineNodePresetState> Nodes { get; set; } = [];
    public List<VisualPipelineStagePresetState> Stages { get; set; } = [];
}

public sealed class VisualPipelineNodePresetState
{
    public int NodeId { get; set; }
    public string NodeKind { get; set; } = "Stage";
    public string? StageTypeId { get; set; }
    public int? InputAId { get; set; }
    public int? InputBId { get; set; }
    public List<int?> InputExtraIds { get; set; } = [];
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public Dictionary<string, float> ParameterValues { get; set; } = [];
    public List<string> SourceImagePaths { get; set; } = [];
}

public sealed class VisualPipelineStagePresetState
{
    public string StageTypeId { get; set; } = string.Empty;
    public string InputSource { get; set; } = "Previous";
    public Dictionary<string, float> ParameterValues { get; set; } = [];
}
