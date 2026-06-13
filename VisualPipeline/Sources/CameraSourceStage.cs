namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class CameraSourceStage : PipelineStage
    {
        public const string TypeIdValue = "camera.source";
        private static readonly IReadOnlyList<IParameter> EmptyParameters = [];

        public override string TypeId => TypeIdValue;
        public override string Name => "Camera Source";
        public override IReadOnlyList<IParameter> Parameters => EmptyParameters;
        public override bool SupportsInputSelection => false;

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            var cameraTexture = host._cameraInput?.TextureId ?? 0;
            host.DrawFullscreen(host._blitProgramFlipY, cameraTexture);
        }
    }
}
