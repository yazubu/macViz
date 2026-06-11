namespace macViz;

public interface IParameter
{
    string Name { get; }
}

public sealed class Parameter<T> : IParameter where T : struct
{
    public string Name { get; }
    public T Min { get; }
    public T Max { get; }
    public T Value { get; set; }

    public Parameter(string name, T min, T max, T value)
    {
        Name = name;
        Min = min;
        Max = max;
        Value = value;
    }
}
