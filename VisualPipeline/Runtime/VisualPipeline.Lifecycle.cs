using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private void RefreshDynamicStageParameters()
    {
        var changed = false;
        foreach (var node in _nodes)
        {
            if (node.Stage is not null && node.Stage.RefreshDynamicParameters())
            {
                changed = true;
            }
        }

        if (changed)
        {
            RebuildParameters();
        }
    }

    private void RebuildParameters()
    {
        _parameters.Clear();
        foreach (var node in _nodes)
        {
            _parameters.AddRange(node.GetAllParameters());
        }

        _parameters.AddRange(GetOutputRecorderParameters());
    }

    private void BuildDefaultPipeline()
    {
        ClearStages(deferDispose: true);

        var camera = new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Stage,
            Stage = new CameraSourceStage(),
            Position = new System.Numerics.Vector2(40f, 80f)
        };

        var edge = new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Stage,
            Stage = new EdgeDetectEffectStage(),
            InputAId = camera.Id,
            Position = new System.Numerics.Vector2(360f, 80f)
        };

        var snapshot = new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Stage,
            Stage = new SnapshotPeakEffectStage(),
            InputAId = edge.Id,
            Position = new System.Numerics.Vector2(680f, 80f)
        };

        var output = new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Output,
            InputAId = snapshot.Id,
            Position = new System.Numerics.Vector2(1000f, 94f)
        };

        _nodes.Add(camera);
        _nodes.Add(edge);
        _nodes.Add(snapshot);
        _nodes.Add(output);

        _selectedNodeId = camera.Id;
        _linkStartNodeId = null;
        RebuildParameters();
    }

    private static float GetParameterNumericValue(IParameter parameter)
    {
        return parameter switch
        {
            Parameter<int> intParameter => intParameter.Value,
            Parameter<float> floatParameter => floatParameter.Value,
            _ => 0f
        };
    }

    private static void SetParameterNumericValue(IParameter parameter, float numericValue)
    {
        switch (parameter)
        {
            case Parameter<int> intParameter:
                intParameter.Value = Math.Clamp((int)MathF.Round(numericValue), intParameter.Min, intParameter.Max);
                break;
            case Parameter<float> floatParameter:
                floatParameter.Value = Math.Clamp(numericValue, floatParameter.Min, floatParameter.Max);
                break;
        }
    }

    private static PipelineStage? CreateStageByTypeId(string typeId)
    {
        var factory = StageFactories.FirstOrDefault(x => x.TypeId == typeId);
        return factory?.Create();
    }

    private void ProcessPendingDisposals()
    {
        while (_pendingDisposals.Count > 0)
        {
            var stage = _pendingDisposals.Dequeue();
            stage.Dispose();
        }
    }

    private void ClearStages(bool deferDispose)
    {
        foreach (var node in _nodes)
        {
            if (node.Stage is null)
            {
                continue;
            }

            if (deferDispose)
            {
                _pendingDisposals.Enqueue(node.Stage);
            }
            else
            {
                node.Stage.Dispose();
            }
        }

        _nodes.Clear();

        foreach (var texture in _nodeOutputTextures.Values)
        {
            if (texture != 0)
            {
                GL.DeleteTexture(texture);
            }
        }

        _nodeOutputTextures.Clear();
        _staticImagePathDraftByNode.Clear();
        _outputRecorder.Dispose();
        _outputRecorderPathDraft = string.Empty;
        _selectedNodeId = null;
        _linkStartNodeId = null;
    }

    public void Dispose()
    {
        ClearStages(deferDispose: false);
        ProcessPendingDisposals();

        if (_copyFboRead != 0) GL.DeleteFramebuffer(_copyFboRead);
        if (_copyFboDraw != 0) GL.DeleteFramebuffer(_copyFboDraw);
        if (_stageFbo != 0) GL.DeleteFramebuffer(_stageFbo);
        if (_stageTexture != 0) GL.DeleteTexture(_stageTexture);

        if (_quadEbo != 0) GL.DeleteBuffer(_quadEbo);
        if (_quadVbo != 0) GL.DeleteBuffer(_quadVbo);
        if (_quadVao != 0) GL.DeleteVertexArray(_quadVao);

        _outputRecorder.Dispose();

        if (_blitProgram != 0) GL.DeleteProgram(_blitProgram);
        if (_blitProgramFlipY != 0) GL.DeleteProgram(_blitProgramFlipY);
        if (_blendProgram != 0) GL.DeleteProgram(_blendProgram);
    }
}
