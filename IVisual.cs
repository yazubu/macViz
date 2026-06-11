namespace macViz;

public interface IVisual : IDisposable
{
    string Name { get; }
    IReadOnlyList<IParameter> Parameters { get; }
    void Render(float[] spectrum, float time);
}
