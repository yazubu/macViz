namespace macViz;

public interface IVisual : IDisposable
{
    string Name { get; }
    void Render(float[] spectrum, float time);
}
