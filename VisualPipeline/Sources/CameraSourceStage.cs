namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class CameraSourceStage : PipelineStage
    {
        public const string TypeIdValue = "camera.source";

        private readonly Parameter<int> _deviceIndex = new("Camera Source / Device Index", 0, 32, 0);
        private readonly List<int> _availableDeviceIndices = [];

        private int _activeDeviceIndex = -1;
        private string _cameraStatus = "Not initialized";
        private string? _deviceWarning;

        public CameraSourceStage()
        {
            RefreshDevices();
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Camera Source";
        public override IReadOnlyList<IParameter> Parameters => [_deviceIndex];
        public override bool IsSourceStage => true;

        public IReadOnlyList<int> AvailableDeviceIndices => _availableDeviceIndices;
        public int SelectedDeviceIndex => _deviceIndex.Value;
        public string CameraStatus => _cameraStatus;
        public bool HasDeviceWarning => !string.IsNullOrWhiteSpace(_deviceWarning);
        public string DeviceWarning => _deviceWarning ?? string.Empty;

        public void RefreshDevices()
        {
            CameraSourceRegistry.RefreshDevicesAndWarmup();
            _availableDeviceIndices.Clear();
            _availableDeviceIndices.AddRange(CameraSourceRegistry.GetEnabledAvailableDeviceIndices());

            if (_availableDeviceIndices.Count == 0)
            {
                _cameraStatus = "No enabled camera devices found";
            }
        }

        public void SetSelectedDeviceIndex(int deviceIndex)
        {
            if (_deviceIndex.Value == deviceIndex)
            {
                return;
            }

            _deviceIndex.Value = deviceIndex;
            _deviceWarning = null;
            _cameraStatus = $"Switching to device {_deviceIndex.Value}...";
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            _availableDeviceIndices.Clear();
            _availableDeviceIndices.AddRange(CameraSourceRegistry.GetEnabledAvailableDeviceIndices());

            if (!CameraSourceRegistry.TryResolveDevice(_deviceIndex.Value, out var resolvedDeviceIndex, out var warning))
            {
                _activeDeviceIndex = -1;
                _cameraStatus = "No enabled camera devices available";
                _deviceWarning = warning;
                host.DrawFullscreen(host._blitProgramFlipY, 0);
                return;
            }

            if (resolvedDeviceIndex != _deviceIndex.Value)
            {
                _deviceWarning = warning;
                _deviceIndex.Value = resolvedDeviceIndex;
            }
            else
            {
                _deviceWarning = null;
            }

            if (!CameraSourceRegistry.TryGetOrCreateInput(resolvedDeviceIndex, out var cameraInput, out var error) || cameraInput is null)
            {
                _activeDeviceIndex = -1;
                _cameraStatus = $"Failed to open device {resolvedDeviceIndex}: {error}";
                host.DrawFullscreen(host._blitProgramFlipY, 0);
                return;
            }

            cameraInput.UpdateTextureFromLatestFrame();
            _activeDeviceIndex = resolvedDeviceIndex;
            _cameraStatus = $"Running (device {_activeDeviceIndex})";
            host.DrawFullscreen(host._blitProgramFlipY, cameraInput.TextureId);
        }

        public override void Dispose()
        {
            _activeDeviceIndex = -1;
        }
    }
}
