using ImGuiNET;
using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private void AddStageNode(PipelineStage stage)
    {
        var node = new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Stage,
            Stage = stage,
            Position = GetAutoNodePosition(_nodes.Count)
        };

        if (!stage.IsSourceStage)
        {
            node.InputAId = GetLastRenderableNodeId();
        }

        InsertBeforeOutputNode(node);
        _selectedNodeId = node.Id;
        RebuildParameters();
    }

    private void AddMixNode()
    {
        var previous = GetLastTwoRenderableNodeIds();
        var node = new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Mix,
            MixBox = new MixBoxNode(),
            InputAId = previous.First,
            InputBId = previous.Second,
            Position = GetAutoNodePosition(_nodes.Count)
        };

        InsertBeforeOutputNode(node);
        _selectedNodeId = node.Id;
        RebuildParameters();
    }

    private void InsertBeforeOutputNode(PipelineNode node)
    {
        var outputIndex = _nodes.FindIndex(x => x.Kind == PipelineNodeKind.Output);
        if (outputIndex < 0)
        {
            _nodes.Add(node);
            EnsureOutputNode();
            return;
        }

        _nodes.Insert(outputIndex, node);
    }

    private void EnsureOutputNode()
    {
        if (_nodes.Any(x => x.Kind == PipelineNodeKind.Output))
        {
            return;
        }

        _nodes.Add(new PipelineNode
        {
            Id = _nextNodeId++,
            Kind = PipelineNodeKind.Output,
            InputAId = GetLastRenderableNodeId(),
            Position = GetAutoNodePosition(Math.Max(0, _nodes.Count))
        });
    }

    private int? GetLastRenderableNodeId()
    {
        for (var i = _nodes.Count - 1; i >= 0; i--)
        {
            if (_nodes[i].Kind != PipelineNodeKind.Output)
            {
                return _nodes[i].Id;
            }
        }

        return null;
    }

    private (int? First, int? Second) GetLastTwoRenderableNodeIds()
    {
        int? first = null;
        int? second = null;

        for (var i = _nodes.Count - 1; i >= 0; i--)
        {
            if (_nodes[i].Kind == PipelineNodeKind.Output)
            {
                continue;
            }

            if (first is null)
            {
                first = _nodes[i].Id;
            }
            else
            {
                second = _nodes[i].Id;
                break;
            }
        }

        return (first, second);
    }

    private void DrawInputNodeSelector(string label, int currentNodeIndex, ref int? selectedNodeId, bool allowCamera)
    {
        var preview = selectedNodeId.HasValue
            ? GetNodeLabel(selectedNodeId.Value)
            : "None";

        if (!ImGui.BeginCombo(label, preview))
        {
            return;
        }

        var isNone = !selectedNodeId.HasValue;
        if (ImGui.Selectable("None", isNone))
        {
            selectedNodeId = null;
        }

        if (isNone)
        {
            ImGui.SetItemDefaultFocus();
        }

        for (var i = 0; i < currentNodeIndex; i++)
        {
            var candidate = _nodes[i];
            if (candidate.Kind == PipelineNodeKind.Output)
            {
                continue;
            }

            var selected = selectedNodeId == candidate.Id;
            var candidateLabel = GetNodeLabel(candidate.Id);
            if (ImGui.Selectable(candidateLabel, selected))
            {
                selectedNodeId = candidate.Id;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    private string GetNodeLabel(int nodeId)
    {
        if (nodeId == CameraVirtualNodeId)
        {
            return "None";
        }

        var node = _nodes.FirstOrDefault(x => x.Id == nodeId);
        return node is null ? $"Node {nodeId}" : GetNodeLabel(node);
    }

    private static string GetNodeLabel(PipelineNode node)
    {
        return node.Kind switch
        {
            PipelineNodeKind.Stage when node.Stage is not null => $"[{node.Id}] {node.Stage.Name}",
            PipelineNodeKind.Mix => $"[{node.Id}] Mix Box",
            PipelineNodeKind.Output => $"[{node.Id}] Out Box",
            _ => $"[{node.Id}] Node"
        };
    }

    private int ResolveNodeInputTexture(int? inputNodeId, int fallbackTexture, IReadOnlyDictionary<int, int> renderedOutputs, bool allowCameraFallback)
    {
        if (inputNodeId.HasValue && renderedOutputs.TryGetValue(inputNodeId.Value, out var texture))
        {
            return texture;
        }

        if (allowCameraFallback)
        {
            return fallbackTexture;
        }

        return 0;
    }

    private static void EnsureSignalSwitchNodeInputs(PipelineNode node)
    {
        while (node.InputExtraIds.Count < SignalSwitchStage.MaxInputs - 1)
        {
            node.InputExtraIds.Add(null);
        }

        if (node.InputExtraIds.Count > SignalSwitchStage.MaxInputs - 1)
        {
            node.InputExtraIds.RemoveRange(SignalSwitchStage.MaxInputs - 1, node.InputExtraIds.Count - (SignalSwitchStage.MaxInputs - 1));
        }
    }

    private void RenderSignalSwitchNode(int selectedTexture, int outputTexture)
    {
        AttachTextureToStageFbo(outputTexture);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (selectedTexture != 0)
        {
            DrawFullscreen(_blitProgram, selectedTexture);
        }
    }

    private void RenderStageNode(PipelineStage stage, int inputTexture, int outputTexture, float[] spectrum, float time)
    {
        AttachTextureToStageFbo(_stageTexture);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        stage.EnsureResources(this);
        stage.Render(this, inputTexture, spectrum, time);

        AttachTextureToStageFbo(outputTexture);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (stage.IsSourceStage)
        {
            DrawFullscreen(_blitProgram, _stageTexture);
        }
        else
        {
            DrawBlendTextures(inputTexture, _stageTexture, stage.StageMix.CurrentValue, stage.BlendMode.CurrentValue);
        }
    }

    private void RenderMixNode(MixBoxNode mixBox, int inputA, int inputB, int outputTexture)
    {
        AttachTextureToStageFbo(outputTexture);
        GL.Viewport(0, 0, _renderWidth, _renderHeight);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        DrawBlendTextures(inputA, inputB, mixBox.Mix.CurrentValue, mixBox.BlendMode.CurrentValue);
    }

    private void DrawBlendTextures(int baseTexture, int layerTexture, float mix, int blendMode)
    {
        GL.UseProgram(_blendProgram);
        GL.Uniform1(_uBlendMix, mix);
        GL.Uniform1(_uBlendMode, blendMode);
        DrawFullscreenWithTextures(_blendProgram, (0, baseTexture), (1, layerTexture));
    }

    private void AttachTextureToStageFbo(int textureId)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _stageFbo);
        GL.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            textureId,
            0);
    }

    private int EnsureNodeOutputTexture(int nodeId)
    {
        if (_renderWidth <= 0 || _renderHeight <= 0)
        {
            return 0;
        }

        if (_nodeOutputTextures.TryGetValue(nodeId, out var texture) && texture != 0)
        {
            return texture;
        }

        texture = CreateRenderTexture(_renderWidth, _renderHeight);
        _nodeOutputTextures[nodeId] = texture;
        return texture;
    }

    private void RemoveNodeAt(int index)
    {
        var node = _nodes[index];
        if (node.Stage is not null)
        {
            _pendingDisposals.Enqueue(node.Stage);
        }

        _nodes.RemoveAt(index);
        if (_nodeOutputTextures.TryGetValue(node.Id, out var texture) && texture != 0)
        {
            GL.DeleteTexture(texture);
            _nodeOutputTextures.Remove(node.Id);
        }

        _staticImagePathDraftByNode.Remove(node.Id);

        if (_selectedNodeId == node.Id)
        {
            _selectedNodeId = null;
        }

        if (_linkStartNodeId == node.Id)
        {
            _linkStartNodeId = null;
        }

        SanitizeNodeConnections();
        RebuildParameters();
    }

    private void SanitizeNodeConnections()
    {
        var liveIds = new HashSet<int>(_nodes.Select(x => x.Id));
        foreach (var node in _nodes)
        {
            if (node.Kind == PipelineNodeKind.Stage && node.Stage is not null && node.Stage.IsSourceStage)
            {
                node.InputAId = null;
            }
            else if (node.InputAId.HasValue && !liveIds.Contains(node.InputAId.Value))
            {
                node.InputAId = null;
            }

            if (node.InputBId.HasValue && !liveIds.Contains(node.InputBId.Value))
            {
                node.InputBId = null;
            }

            for (var i = 0; i < node.InputExtraIds.Count; i++)
            {
                if (node.InputExtraIds[i].HasValue && !liveIds.Contains(node.InputExtraIds[i]!.Value))
                {
                    node.InputExtraIds[i] = null;
                }
            }

            if (node.Stage is SignalSwitchStage)
            {
                EnsureSignalSwitchNodeInputs(node);
            }
            else
            {
                node.InputExtraIds.Clear();
            }
        }

        EnsureOutputNode();
        ReorderNodesTopologically();

        var outputNodes = _nodes.Where(x => x.Kind == PipelineNodeKind.Output).ToList();
        if (outputNodes.Count > 1)
        {
            for (var i = 1; i < outputNodes.Count; i++)
            {
                var duplicate = outputNodes[i];
                _nodes.Remove(duplicate);
            }
        }

        var outputNode = _nodes.FirstOrDefault(x => x.Kind == PipelineNodeKind.Output);
        if (outputNode is not null)
        {
            _nodes.Remove(outputNode);
            _nodes.Add(outputNode);
        }
    }

    private void ReorderNodesTopologically()
    {
        if (_nodes.Count <= 1)
        {
            return;
        }

        var ids = _nodes.Select(x => x.Id).ToHashSet();
        var originalIndex = new Dictionary<int, int>(_nodes.Count);
        for (var i = 0; i < _nodes.Count; i++)
        {
            originalIndex[_nodes[i].Id] = i;
        }

        var inDegree = _nodes.ToDictionary(x => x.Id, _ => 0);
        var adjacency = _nodes.ToDictionary(x => x.Id, _ => new List<int>());

        foreach (var node in _nodes)
        {
            if (node.InputAId.HasValue && ids.Contains(node.InputAId.Value) && node.InputAId.Value != node.Id)
            {
                adjacency[node.InputAId.Value].Add(node.Id);
                inDegree[node.Id]++;
            }

            if (node.InputBId.HasValue && ids.Contains(node.InputBId.Value) && node.InputBId.Value != node.Id)
            {
                adjacency[node.InputBId.Value].Add(node.Id);
                inDegree[node.Id]++;
            }

            foreach (var extraInputId in node.InputExtraIds)
            {
                if (!extraInputId.HasValue || !ids.Contains(extraInputId.Value) || extraInputId.Value == node.Id)
                {
                    continue;
                }

                adjacency[extraInputId.Value].Add(node.Id);
                inDegree[node.Id]++;
            }
        }

        var available = _nodes
            .Where(x => inDegree[x.Id] == 0)
            .OrderBy(x => originalIndex[x.Id])
            .Select(x => x.Id)
            .ToList();

        var orderedIds = new List<int>(_nodes.Count);
        while (available.Count > 0)
        {
            var nextId = available[0];
            available.RemoveAt(0);
            orderedIds.Add(nextId);

            foreach (var dependentId in adjacency[nextId])
            {
                inDegree[dependentId]--;
                if (inDegree[dependentId] == 0)
                {
                    available.Add(dependentId);
                }
            }

            available.Sort((a, b) => originalIndex[a].CompareTo(originalIndex[b]));
        }

        if (orderedIds.Count != _nodes.Count)
        {
            return;
        }

        var byId = _nodes.ToDictionary(x => x.Id);
        _nodes.Clear();
        foreach (var id in orderedIds)
        {
            _nodes.Add(byId[id]);
        }
    }

    private void ApplyGraphPresetState(VisualPipelinePresetState state)
    {
        ClearStages(deferDispose: true);
        _outputRecorderTrigger.Value = 0f;
        _outputRecorderFps.Value = 30f;
        _outputRecorderCompress.Value = 1;
        _outputRecorderCrf.Value = 23;
        _outputRecorderPreset.Value = 0;
        _outputRecorder.SetOutputDirectory(string.Empty);
        _outputRecorderPathDraft = string.Empty;

        var idMap = new Dictionary<int, int>();
        var nodeStateByNewId = new Dictionary<int, VisualPipelineNodePresetState>();

        foreach (var nodeState in state.Nodes)
        {
            if (!Enum.TryParse<PipelineNodeKind>(nodeState.NodeKind, out var kind))
            {
                continue;
            }

            PipelineNode? node = kind switch
            {
                PipelineNodeKind.Stage when !string.IsNullOrWhiteSpace(nodeState.StageTypeId) => new PipelineNode
                {
                    Id = _nextNodeId++,
                    Kind = PipelineNodeKind.Stage,
                    Stage = CreateStageByTypeId(nodeState.StageTypeId!)
                },
                PipelineNodeKind.Mix => new PipelineNode
                {
                    Id = _nextNodeId++,
                    Kind = PipelineNodeKind.Mix,
                    MixBox = new MixBoxNode()
                },
                PipelineNodeKind.Output => new PipelineNode
                {
                    Id = _nextNodeId++,
                    Kind = PipelineNodeKind.Output
                },
                _ => null
            };

            if (node is null || (kind == PipelineNodeKind.Stage && node.Stage is null))
            {
                continue;
            }

            node.Position = new System.Numerics.Vector2(nodeState.PositionX, nodeState.PositionY);

            foreach (var parameter in node.GetAllParameters())
            {
                if (nodeState.ParameterValues.TryGetValue(parameter.Name, out var numericValue))
                {
                    SetParameterNumericValue(parameter, numericValue);
                }
            }

            if (node.Stage is StaticImageSourceStage staticImageSourceStage && nodeState.SourceImagePaths.Count > 0)
            {
                staticImageSourceStage.SetImagePaths(nodeState.SourceImagePaths);
            }

            if (node.Kind == PipelineNodeKind.Output)
            {
                if (!string.IsNullOrWhiteSpace(nodeState.RecorderOutputDirectory))
                {
                    _outputRecorder.SetOutputDirectory(nodeState.RecorderOutputDirectory);
                }

                if (nodeState.RecorderTrigger.HasValue)
                {
                    _outputRecorderTrigger.Value = Math.Clamp(nodeState.RecorderTrigger.Value, 0f, 1f);
                }

                if (nodeState.RecorderFps.HasValue)
                {
                    _outputRecorderFps.Value = Math.Clamp(nodeState.RecorderFps.Value, 1f, 120f);
                }

                if (nodeState.RecorderCompress.HasValue)
                {
                    _outputRecorderCompress.Value = Math.Clamp(nodeState.RecorderCompress.Value, 0, 1);
                }

                if (nodeState.RecorderCrf.HasValue)
                {
                    _outputRecorderCrf.Value = Math.Clamp(nodeState.RecorderCrf.Value, 0, 51);
                }

                if (nodeState.RecorderPreset.HasValue)
                {
                    _outputRecorderPreset.Value = Math.Clamp(nodeState.RecorderPreset.Value, 0, 8);
                }
            }

            _nodes.Add(node);
            idMap[nodeState.NodeId] = node.Id;
            nodeStateByNewId[node.Id] = nodeState;
        }

        foreach (var node in _nodes)
        {
            if (!nodeStateByNewId.TryGetValue(node.Id, out var nodeState))
            {
                continue;
            }

            node.InputAId = nodeState.InputAId.HasValue && idMap.TryGetValue(nodeState.InputAId.Value, out var mappedA) ? mappedA : null;
            node.InputBId = nodeState.InputBId.HasValue && idMap.TryGetValue(nodeState.InputBId.Value, out var mappedB) ? mappedB : null;
            node.InputExtraIds.Clear();
            foreach (var inputId in nodeState.InputExtraIds)
            {
                node.InputExtraIds.Add(inputId.HasValue && idMap.TryGetValue(inputId.Value, out var mappedExtra) ? mappedExtra : null);
            };
        }

        SanitizeNodeConnections();
        if (_nodes.Count == 0)
        {
            BuildDefaultPipeline();
            return;
        }

        _selectedNodeId = _nodes[0].Id;
        _linkStartNodeId = null;
        RebuildParameters();
    }

    private void ApplyLegacySequentialPresetState(IReadOnlyList<VisualPipelineStagePresetState> stages)
    {
        ClearStages(deferDispose: true);

        PipelineNode? previous = null;
        foreach (var stageState in stages)
        {
            var stage = CreateStageByTypeId(stageState.StageTypeId);
            if (stage is null)
            {
                continue;
            }

            var node = new PipelineNode
            {
                Id = _nextNodeId++,
                Kind = PipelineNodeKind.Stage,
                Stage = stage,
                InputAId = stage.IsSourceStage ? null : previous?.Id,
                Position = GetAutoNodePosition(_nodes.Count)
            };

            foreach (var parameter in stage.GetAllParameters())
            {
                if (stageState.ParameterValues.TryGetValue(parameter.Name, out var numericValue))
                {
                    SetParameterNumericValue(parameter, numericValue);
                }
            }

            if (stage.RefreshDynamicParameters())
            {
                foreach (var parameter in stage.GetAllParameters())
                {
                    if (stageState.ParameterValues.TryGetValue(parameter.Name, out var numericValue))
                    {
                        SetParameterNumericValue(parameter, numericValue);
                    }
                }
            }

            _nodes.Add(node);
            previous = node;
        }

        EnsureOutputNode();
        var output = _nodes.FirstOrDefault(x => x.Kind == PipelineNodeKind.Output);
        if (output is not null)
        {
            output.InputAId = previous?.Id;
        }

        if (_nodes.Count == 0)
        {
            BuildDefaultPipeline();
            return;
        }

        if (output is not null)
        {
            output.Position = GetAutoNodePosition(Math.Max(0, _nodes.Count - 1));
        }

        _selectedNodeId = _nodes[0].Id;
        _linkStartNodeId = null;
        RebuildParameters();
    }

}
