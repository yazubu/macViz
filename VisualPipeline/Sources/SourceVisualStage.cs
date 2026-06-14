namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class SourceVisualStage(string label, IVisual visual, string typeId) : PipelineStage
    {
        public const string RotatingCubeTypeId = "source.rotatingCube";
        public const string SpectrumBarsTypeId = "source.spectrumBars";
        public const string ParticleSystemTypeId = "source.particleSystem";
        public const string CymaticSpiralsTypeId = "source.cymaticSpirals";
        public const string DiffusionPaintingTypeId = "source.diffusionPainting";

        public override string TypeId => typeId;
        public override string Name => label;
        public override IReadOnlyList<IParameter> Parameters => visual.Parameters;
        public override bool IsSourceStage => true;

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            visual.Render(spectrum, time);
        }

        public override void Dispose()
        {
            visual.Dispose();
        }
    }
}
