namespace macViz;

internal static class LfoDisplayHelpers
{
    public static string GetSyncSpeedLabel(float multiplier, IReadOnlyList<(string Label, float Multiplier)> options)
    {
        foreach (var option in options)
        {
            if (MathF.Abs(option.Multiplier - multiplier) < 0.0001f)
            {
                return option.Label;
            }
        }

        return $"x{multiplier:0.##}";
    }
}
