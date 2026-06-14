namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed record StageFactory(string TypeId, string Label, Func<PipelineStage> Create);

    private enum PipelineNodeKind
    {
        Stage,
        Mix,
        Output
    }

    private abstract class PipelineStage : IDisposable
    {
        private readonly Parameter<float> _stageMix = new("Stage / Mix", 0f, 1f, 1f);
        private readonly Parameter<int> _blendMode = new("Stage / Blend Mode (0 Normal,1 Subtract,2 Multiply,3 Darker,4 Brighter,5 Screen,6 Difference,7 Overlay,8 Hard Light,9 Divide,10 Color Dodge)", 0, 10, 0);

        public abstract string TypeId { get; }
        public abstract string Name { get; }
        public abstract IReadOnlyList<IParameter> Parameters { get; }
        public virtual bool IsSourceStage => false;
        public Parameter<float> StageMix => _stageMix;
        public Parameter<int> BlendMode => _blendMode;

        public IEnumerable<IParameter> GetAllParameters()
        {
            yield return _stageMix;
            yield return _blendMode;

            foreach (var parameter in Parameters)
            {
                yield return parameter;
            }
        }

        public virtual bool RefreshDynamicParameters()
        {
            return false;
        }

        public virtual void EnsureResources(VisualPipeline host)
        {
        }

        public virtual void OnResize(int width, int height, VisualPipeline host)
        {
        }

        public abstract void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time);

        public virtual void Dispose()
        {
        }
    }

    private sealed class MixBoxNode
    {
        private readonly Parameter<float> _mix = new("Mix Box / Mix", 0f, 1f, 1f);
        private readonly Parameter<int> _blendMode = new("Mix Box / Blend Mode (0 Normal,1 Subtract,2 Multiply,3 Darker,4 Brighter,5 Screen,6 Difference,7 Overlay,8 Hard Light,9 Divide,10 Color Dodge)", 0, 10, 0);

        public Parameter<float> Mix => _mix;
        public Parameter<int> BlendMode => _blendMode;

        public IEnumerable<IParameter> GetAllParameters()
        {
            yield return _mix;
            yield return _blendMode;
        }
    }

    private sealed class PipelineNode
    {
        public int Id;
        public PipelineNodeKind Kind;
        public PipelineStage? Stage;
        public MixBoxNode? MixBox;
        public int? InputAId;
        public int? InputBId;
        public List<int?> InputExtraIds = [];
        public System.Numerics.Vector2 Position;

        public IEnumerable<IParameter> GetAllParameters()
        {
            if (Stage is not null)
            {
                foreach (var parameter in Stage.GetAllParameters())
                {
                    yield return parameter;
                }
            }

            if (MixBox is not null)
            {
                foreach (var parameter in MixBox.GetAllParameters())
                {
                    yield return parameter;
                }
            }
        }
    }
}
