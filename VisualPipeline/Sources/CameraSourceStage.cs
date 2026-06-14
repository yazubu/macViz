namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class CameraSourceStage : PipelineStage
    {
        public const string TypeIdValue = "camera.source";

        private readonly Parameter<int> _deviceIndex = new("Camera Source / Device Index", 0, 32, 0);
        private readonly List<int> _availableDeviceIndices = [];

        private CameraInput? _cameraInput;
        private bool _cameraReinitPending;
        private int _activeDeviceIndex = -1;
        private string _cameraStatus = "Not initialized";

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

        public void RefreshDevices()
        {
            _availableDeviceIndices.Clear();
            _availableDeviceIndices.AddRange(CameraInput.EnumerateDeviceIndices());

            if (_availableDeviceIndices.Count == 0)
            {
                _cameraStatus = "No camera devices found";
                return;
            }

            if (!_availableDeviceIndices.Contains(_deviceIndex.Value))
            {
                _deviceIndex.Value = _availableDeviceIndices[0];
                _cameraReinitPending = true;
                _cameraStatus = $"Switching to device {_deviceIndex.Value}...";
            }
        }

        public void SetSelectedDeviceIndex(int deviceIndex)
        {
            if (_deviceIndex.Value == deviceIndex)
            {
                return;
            }

            _deviceIndex.Value = deviceIndex;
            _cameraReinitPending = true;
            _cameraStatus = $"Switching to device {_deviceIndex.Value}...";
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            if (_deviceIndex.Value != _activeDeviceIndex)
            {
                _cameraReinitPending = true;
            }

            if (_cameraReinitPending)
            {
                _cameraInput?.Dispose();
                _cameraInput = null;
                _cameraReinitPending = false;
                _cameraStatus = $"Reinitializing device {_deviceIndex.Value}...";
            }

            if (_cameraInput is null)
            {
                try
                {
                    _cameraInput = new CameraInput(_deviceIndex.Value);
                    _activeDeviceIndex = _deviceIndex.Value;
                    _cameraStatus = $"Running (device {_activeDeviceIndex})";
                }
                catch (Exception ex)
                {
                    _cameraInput = null;
                    _activeDeviceIndex = -1;
                    _cameraStatus = $"Failed to open device {_deviceIndex.Value}: {ex.Message}";
                }
            }

            _cameraInput?.UpdateTextureFromLatestFrame();
            var cameraTexture = _cameraInput?.TextureId ?? 0;
            host.DrawFullscreen(host._blitProgramFlipY, cameraTexture);
        }

        public override void Dispose()
        {
            _cameraInput?.Dispose();
            _cameraInput = null;
            _activeDeviceIndex = -1;
        }
    }
}
