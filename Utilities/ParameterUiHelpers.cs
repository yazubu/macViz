namespace macViz;

internal static class ParameterUiHelpers
{
    public static bool IsStageCoreParameter(string parameterName)
    {
        return parameterName.StartsWith("Stage / ", StringComparison.Ordinal);
    }

    public static string GetParameterSectionName(IVisual visual, IParameter parameter)
    {
        if (visual is VisualPipeline visualPipeline &&
            visualPipeline.TryGetNodeDescriptorForParameter(parameter, out var nodeLabel))
        {
            return nodeLabel;
        }

        const string separator = " / ";
        var separatorIndex = parameter.Name.IndexOf(separator, StringComparison.Ordinal);
        return separatorIndex > 0 ? parameter.Name[..separatorIndex] : "General";
    }

    public static string GetParameterDisplayName(string parameterName)
    {
        const string separator = " / ";
        var separatorIndex = parameterName.IndexOf(separator, StringComparison.Ordinal);
        return separatorIndex > 0 ? parameterName[(separatorIndex + separator.Length)..] : parameterName;
    }

    public static string GetModMatrixSectionName(IVisual visual, IParameter parameter)
    {
        if (visual is VisualPipeline visualPipeline &&
            visualPipeline.TryGetNodeDescriptorForParameter(parameter, out var nodeLabel))
        {
            return nodeLabel;
        }

        return GetParameterSectionName(visual, parameter);
    }

    public static string GetModMatrixParameterLabel(IParameter parameter)
    {
        return GetParameterDisplayName(parameter.Name);
    }

    public static IReadOnlyList<(string SectionName, List<int> ParameterIndices)> BuildParameterSections(
        IVisual visual,
        Func<IVisual, IParameter, string> sectionNameSelector)
    {
        var orderedSections = new List<(string SectionName, List<int> ParameterIndices)>();
        var sectionLookup = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var i = 0; i < visual.Parameters.Count; i++)
        {
            var parameter = visual.Parameters[i];
            var sectionName = sectionNameSelector(visual, parameter);

            if (!sectionLookup.TryGetValue(sectionName, out var parameterIndices))
            {
                parameterIndices = [];
                sectionLookup[sectionName] = parameterIndices;
                orderedSections.Add((sectionName, parameterIndices));
            }

            parameterIndices.Add(i);
        }

        return orderedSections;
    }

    public static void AdjustBaseParameter(IParameter parameter, int direction, bool fast)
    {
        switch (parameter)
        {
            case Parameter<int> intParameter:
            {
                var step = fast ? 4 : 1;
                intParameter.Value = Math.Clamp(intParameter.Value + (direction * step), intParameter.Min, intParameter.Max);
                break;
            }
            case Parameter<float> floatParameter:
            {
                var range = floatParameter.Max - floatParameter.Min;
                var step = (fast ? 0.05f : 0.01f) * range;
                floatParameter.Value = Math.Clamp(floatParameter.Value + (direction * step), floatParameter.Min, floatParameter.Max);
                break;
            }
        }
    }

    public static IParameter? ResolvePipelineParameter(IReadOnlyList<IParameter> parameters, int index, string name)
    {
        if (index >= 0 && index < parameters.Count && parameters[index].Name == name)
        {
            return parameters[index];
        }

        return parameters.FirstOrDefault(x => x.Name == name);
    }
}
