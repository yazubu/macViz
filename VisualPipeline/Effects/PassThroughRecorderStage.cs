using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class PassThroughRecorderStage : PipelineStage
    {
        public const string TypeIdValue = "effect.passThroughRecorder";

        private readonly Parameter<float> _trigger = new("Recorder / Trigger", 0f, 1f, 0f);
        private readonly Parameter<float> _fps = new("Recorder / FPS", 1f, 120f, 30f);
        private readonly Parameter<int> _compress = new("Recorder / Compress (0 Off, 1 On)", 0, 1, 1);
        private readonly Parameter<int> _crf = new("Recorder / Compression CRF", 0, 51, 23);
        private readonly Parameter<int> _preset = new("Recorder / Preset (0 UltraFast,1 SuperFast,2 VeryFast,3 Faster,4 Fast,5 Medium,6 Slow,7 Slower,8 VerySlow)", 0, 8, 0);
        private readonly IReadOnlyList<IParameter> _parameters;

        private int _program;
        private int _uTexture;

        private bool _previousTriggerHigh;
        private bool _isRecording;
        private int _frameWidth;
        private int _frameHeight;
        private byte[] _frameBuffer = Array.Empty<byte>();

        private Process? _ffmpegProcess;
        private Stream? _encoderInput;
        private FileStream? _rawOutput;
        private string? _activeFilePath;
        private string _outputDirectory = string.Empty;
        private long _writtenFrames;

        public PassThroughRecorderStage()
        {
            _parameters = [_trigger, _fps, _compress, _crf, _preset];
        }

        public override string TypeId => TypeIdValue;
        public override string Name => "Pass Through Recorder";
        public override IReadOnlyList<IParameter> Parameters => _parameters;
        public bool IsRecording => _isRecording;
        public string? ActiveFilePath => _activeFilePath;
        public string OutputDirectory => _outputDirectory;

        public void SetOutputDirectory(string? path)
        {
            if (_isRecording)
            {
                return;
            }

            _outputDirectory = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();
        }

        public override void EnsureResources(VisualPipeline host)
        {
            if (_program != 0)
            {
                return;
            }

            const string vertex = """
                #version 330 core
                layout (location = 0) in vec2 aPosition;
                layout (location = 1) in vec2 aUv;
                out vec2 vUv;
                void main()
                {
                    vUv = aUv;
                    gl_Position = vec4(aPosition, 0.0, 1.0);
                }
                """;

            const string fragment = """
                #version 330 core
                in vec2 vUv;
                out vec4 fragColor;
                uniform sampler2D uTexture;
                void main()
                {
                    fragColor = texture(uTexture, vUv);
                }
                """;

            _program = CompileProgram(vertex, fragment);
            _uTexture = GL.GetUniformLocation(_program, "uTexture");

            GL.UseProgram(_program);
            GL.Uniform1(_uTexture, 0);
            GL.UseProgram(0);
        }

        public override void OnResize(int width, int height, VisualPipeline host)
        {
            _frameWidth = Math.Max(1, width);
            _frameHeight = Math.Max(1, height);
            EnsureFrameBuffer();

            if (_isRecording)
            {
                StopRecording("stopped due to resize");
            }
        }

        public override void Render(VisualPipeline host, int inputTexture, float[] spectrum, float time)
        {
            host.DrawFullscreen(_program, inputTexture);

            var triggerHigh = _trigger.CurrentValue >= 0.5f;
            if (triggerHigh && !_previousTriggerHigh)
            {
                if (_isRecording)
                {
                    StopRecording("stopped by trigger");
                }
                else
                {
                    StartRecording();
                }
            }

            _previousTriggerHigh = triggerHigh;

            if (!_isRecording || inputTexture == 0)
            {
                return;
            }

            EnsureFrameBuffer();
            if (_frameBuffer.Length == 0)
            {
                return;
            }

            if (!host.TryReadTextureRgba(inputTexture, _frameBuffer))
            {
                return;
            }

            FlipRowsInPlace(_frameBuffer, _frameWidth * 4, _frameHeight);

            try
            {
                _encoderInput?.Write(_frameBuffer, 0, _frameBuffer.Length);
                _encoderInput?.Flush();
                _writtenFrames++;
            }
            catch
            {
                StopRecording("stopped because writer failed");
            }
        }

        public override void Dispose()
        {
            StopRecording("stopped on dispose");

            if (_program != 0)
            {
                GL.DeleteProgram(_program);
                _program = 0;
            }
        }

        private void EnsureFrameBuffer()
        {
            if (_frameWidth <= 0 || _frameHeight <= 0)
            {
                return;
            }

            var requiredSize = _frameWidth * _frameHeight * 4;
            if (_frameBuffer.Length != requiredSize)
            {
                _frameBuffer = new byte[requiredSize];
            }
        }

        private void StartRecording()
        {
            if (_frameWidth <= 0 || _frameHeight <= 0)
            {
                Console.WriteLine("[Recorder] Cannot start recording: invalid frame size.");
                return;
            }

            var recordingsDir = ResolveOutputDirectory();
            Directory.CreateDirectory(recordingsDir);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _writtenFrames = 0;

            if (_compress.CurrentValue > 0 && TryStartFfmpeg(recordingsDir, stamp))
            {
                _isRecording = true;
                Console.WriteLine($"[Recorder] Recording started (compressed): {_activeFilePath}");
                return;
            }

            var rawPath = Path.Combine(recordingsDir, $"capture-{stamp}-{_frameWidth}x{_frameHeight}.rgba");
            _rawOutput = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _encoderInput = _rawOutput;
            _activeFilePath = rawPath;
            _isRecording = true;
            Console.WriteLine($"[Recorder] Recording started (raw): {_activeFilePath}");
        }

        private string ResolveOutputDirectory()
        {
            if (string.IsNullOrWhiteSpace(_outputDirectory))
            {
                return Path.Combine(AppContext.BaseDirectory, "recordings");
            }

            try
            {
                return Path.GetFullPath(_outputDirectory);
            }
            catch
            {
                return Path.Combine(AppContext.BaseDirectory, "recordings");
            }
        }

        private bool TryStartFfmpeg(string recordingsDir, string stamp)
        {
            var outputPath = Path.Combine(recordingsDir, $"capture-{stamp}.mp4");
            var fps = Math.Clamp((int)MathF.Round(_fps.CurrentValue), 1, 120);
            var crf = Math.Clamp(_crf.CurrentValue, 0, 51);
            var preset = GetPresetName(_preset.CurrentValue);

            var args = $"-y -f rawvideo -pixel_format rgba -video_size {_frameWidth}x{_frameHeight} -framerate {fps} -i - -an -c:v libx264 -preset {preset} -crf {crf} -pix_fmt yuv420p \"{outputPath}\"";

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardError = false,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = false
                };

                if (!process.Start())
                {
                    process.Dispose();
                    return false;
                }

                _ffmpegProcess = process;
                _encoderInput = process.StandardInput.BaseStream;
                _activeFilePath = outputPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void StopRecording(string reason)
        {
            if (!_isRecording && _ffmpegProcess is null && _rawOutput is null)
            {
                return;
            }

            try
            {
                _encoderInput?.Flush();
            }
            catch
            {
            }

            try
            {
                _encoderInput?.Dispose();
            }
            catch
            {
            }

            _encoderInput = null;

            try
            {
                _rawOutput?.Dispose();
            }
            catch
            {
            }

            _rawOutput = null;

            if (_ffmpegProcess is not null)
            {
                try
                {
                    if (!_ffmpegProcess.HasExited)
                    {
                        _ffmpegProcess.WaitForExit(3000);
                    }
                }
                catch
                {
                }

                try
                {
                    if (!_ffmpegProcess.HasExited)
                    {
                        _ffmpegProcess.Kill(true);
                    }
                }
                catch
                {
                }

                _ffmpegProcess.Dispose();
                _ffmpegProcess = null;
            }

            if (_isRecording)
            {
                Console.WriteLine($"[Recorder] Recording {reason}. Frames: {_writtenFrames}. File: {_activeFilePath}");
            }

            _isRecording = false;
            _activeFilePath = null;
        }

        private static string GetPresetName(int preset)
        {
            return Math.Clamp(preset, 0, 8) switch
            {
                0 => "ultrafast",
                1 => "superfast",
                2 => "veryfast",
                3 => "faster",
                4 => "fast",
                5 => "medium",
                6 => "slow",
                7 => "slower",
                8 => "veryslow",
                _ => "ultrafast"
            };
        }

        private static void FlipRowsInPlace(byte[] buffer, int rowStride, int rows)
        {
            if (rowStride <= 0 || rows <= 1)
            {
                return;
            }

            var temp = new byte[rowStride];
            var top = 0;
            var bottom = (rows - 1) * rowStride;

            while (top < bottom)
            {
                System.Buffer.BlockCopy(buffer, top, temp, 0, rowStride);
                System.Buffer.BlockCopy(buffer, bottom, buffer, top, rowStride);
                System.Buffer.BlockCopy(temp, 0, buffer, bottom, rowStride);

                top += rowStride;
                bottom -= rowStride;
            }
        }
    }
}
