using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    public void Render(float[] spectrum, float time)
    {
        GL.Disable(EnableCap.ScissorTest);
        ProcessPendingDisposals();

        EnsureRenderTargets();

        var outputNode = _nodes.FirstOrDefault(x => x.Kind == PipelineNodeKind.Output);
        if (outputNode is null)
        {
            BuildDefaultPipeline();
            outputNode = _nodes.FirstOrDefault(x => x.Kind == PipelineNodeKind.Output);
            if (outputNode is null)
            {
                return;
            }
        }

        const int fallbackTexture = 0;
        var renderedOutputs = new Dictionary<int, int>();

        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (node.Kind == PipelineNodeKind.Output)
            {
                continue;
            }

            var targetTexture = EnsureNodeOutputTexture(node.Id);
            if (targetTexture == 0)
            {
                continue;
            }

            switch (node.Kind)
            {
                case PipelineNodeKind.Stage when node.Stage is not null:
                {
                    if (node.Stage is SignalSwitchStage signalSwitchStage)
                    {
                        EnsureSignalSwitchNodeInputs(node);
                        var inputIds = new int?[SignalSwitchStage.MaxInputs];
                        inputIds[0] = node.InputAId;
                        for (var slot = 1; slot < SignalSwitchStage.MaxInputs; slot++)
                        {
                            inputIds[slot] = node.InputExtraIds[slot - 1];
                        }

                        var switchedInputTexture = signalSwitchStage.SelectInputTexture(inputIds, candidateId => ResolveNodeInputTexture(candidateId, fallbackTexture, renderedOutputs, allowCameraFallback: false));
                        RenderSignalSwitchNode(switchedInputTexture, targetTexture);
                    }
                    else
                    {
                        var inputTexture = ResolveNodeInputTexture(node.InputAId, fallbackTexture, renderedOutputs, allowCameraFallback: true);
                        RenderStageNode(node.Stage, inputTexture, targetTexture, spectrum, time);
                    }

                    renderedOutputs[node.Id] = targetTexture;
                    break;
                }
                case PipelineNodeKind.Mix when node.MixBox is not null:
                {
                    var inputA = ResolveNodeInputTexture(node.InputAId, fallbackTexture, renderedOutputs, allowCameraFallback: false);
                    var inputB = ResolveNodeInputTexture(node.InputBId, fallbackTexture, renderedOutputs, allowCameraFallback: false);
                    RenderMixNode(node.MixBox, inputA, inputB, targetTexture);
                    renderedOutputs[node.Id] = targetTexture;
                    break;
                }
            }
        }

        var finalTexture = ResolveNodeInputTexture(outputNode.InputAId, fallbackTexture, renderedOutputs, allowCameraFallback: true);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (finalTexture != 0)
        {
            DrawFullscreen(_blitProgram, finalTexture);
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }
}
