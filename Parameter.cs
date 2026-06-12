namespace macViz;

public interface IParameter
{
    string Name { get; }
    void ApplyCombinedModulation(float modulationAmount);
}

public sealed class Parameter<T> : IParameter where T : struct, IConvertible
{
    public string Name { get; }
    public T Min { get; }
    public T Max { get; }

    // User editable/base value.
    public T Value { get; set; }

    // Value after modulation.
    public T CurrentValue { get; private set; }

    public Parameter(string name, T min, T max, T value)
    {
        Name = name;
        Min = min;
        Max = max;
        Value = value;
        CurrentValue = value;
    }

    public void ApplyCombinedModulation(float modulationAmount)
    {
        var baseValue = Convert.ToSingle(Value);
        var min = Convert.ToSingle(Min);
        var max = Convert.ToSingle(Max);

        var modulated = baseValue * modulationAmount;
        modulated = Math.Clamp(modulated, min, max);

        CurrentValue = FromFloat(modulated);
    }

    private static T FromFloat(float value)
    {
        if (typeof(T) == typeof(int))
        {
            return (T)(object)(int)MathF.Round(value);
        }

        if (typeof(T) == typeof(float))
        {
            return (T)(object)value;
        }

        throw new NotSupportedException($"Parameter type {typeof(T).Name} is not supported for modulation.");
    }
}
