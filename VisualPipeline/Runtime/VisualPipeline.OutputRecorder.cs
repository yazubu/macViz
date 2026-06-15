using System.Diagnostics;

namespace macViz;

public sealed partial class VisualPipeline
{
    private sealed class OutputRecorder : IDisposable
    {
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

        public float Trigger { get; set; }
        public float Fps { get; set; } = 30f;
        public int Compress { get; set; } = 1;
        public int Crf { get; set; } = 23;
        public int Preset { get; set; }

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

        public void ProcessFrame(VisualPipeline host, int sourceTexture)
        {
            _frameWidth = Math.Max(1, host._renderWidth);
            _frameHeight = Math.Max(1, host._renderHeight);
            EnsureFrameBuffer();

            var triggerHigh = Trigger >= 0.5f;
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

            if (!_isRecording || sourceTexture == 0)
            {
                return;
            }

            if (!host.TryReadTextureRgba(sourceTexture, _frameBuffer))
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

        public void Dispose()
        {
            StopRecording("stopped on dispose");
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

            if (Compress > 0 && TryStartFfmpeg(recordingsDir, stamp))
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
            var fps = Math.Clamp((int)MathF.Round(Fps), 1, 120);
            var crf = Math.Clamp(Crf, 0, 51);
            var preset = GetPresetName(Preset);

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
                    }
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

            try { _encoderInput?.Flush(); } catch { }
            try { _encoderInput?.Dispose(); } catch { }
            _encoderInput = null;

            try { _rawOutput?.Dispose(); } catch { }
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
                catch { }

                try
                {
                    if (!_ffmpegProcess.HasExited)
                    {
                        _ffmpegProcess.Kill(true);
                    }
                }
                catch { }

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
