namespace macViz;

public interface ICameraVisual
{
    IReadOnlyList<int> AvailableDeviceIndices { get; }
    int SelectedDeviceIndex { get; }
    string CameraStatus { get; }

    void RefreshDevices();
    void SetSelectedDeviceIndex(int deviceIndex);
}
