using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenTK.Graphics.OpenGL4;

namespace macViz;

public sealed class CameraInput : IDisposable
{
    private readonly object _frameLock = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly VideoCapture _capture;
    private readonly Thread _captureThread;

    private byte[]? _latestBgr;
    private int _latestWidth;
    private int _latestHeight;
    private bool _hasNewFrame;

    private int _textureId;
    private int _textureWidth;
    private int _textureHeight;

    public bool IsRunning { get; private set; }
    public int DeviceIndex { get; }

    public CameraInput(int deviceIndex = 0)
    {
        DeviceIndex = deviceIndex;

        _capture = new VideoCapture(deviceIndex);
        if (!_capture.IsOpened())
        {
            throw new InvalidOperationException($"Unable to open camera device {deviceIndex}.");
        }

        _capture.FrameWidth = 640;
        _capture.FrameHeight = 480;

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = $"CameraCaptureThread-{deviceIndex}"
        };
        _captureThread.Start();

        CreateTexture();
        IsRunning = true;
    }

    public int TextureId => _textureId;

    public static List<int> EnumerateDeviceIndices(int maxProbe = 8)
    {
        var devices = new List<int>();

        for (var i = 0; i < maxProbe; i++)
        {
            using var cap = new VideoCapture(i);
            if (cap.IsOpened())
            {
                devices.Add(i);
            }
        }

        return devices;
    }

    public bool UpdateTextureFromLatestFrame()
    {
        byte[]? data;
        int width;
        int height;

        lock (_frameLock)
        {
            if (!_hasNewFrame || _latestBgr is null)
            {
                return false;
            }

            data = _latestBgr;
            width = _latestWidth;
            height = _latestHeight;
            _hasNewFrame = false;
        }

        GL.BindTexture(TextureTarget.Texture2D, _textureId);

        if (width != _textureWidth || height != _textureHeight)
        {
            _textureWidth = width;
            _textureHeight = height;
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgb,
                width,
                height,
                0,
                PixelFormat.Bgr,
                PixelType.UnsignedByte,
                data);
        }
        else
        {
            GL.TexSubImage2D(
                TextureTarget.Texture2D,
                0,
                0,
                0,
                width,
                height,
                PixelFormat.Bgr,
                PixelType.UnsignedByte,
                data);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        return true;
    }

    private void CaptureLoop()
    {
        using var frame = new Mat();
        using var converted = new Mat();

        while (!_cts.IsCancellationRequested)
        {
            if (!_capture.Read(frame) || frame.Empty())
            {
                Thread.Sleep(8);
                continue;
            }

            Mat src;
            if (frame.Channels() == 3)
            {
                src = frame;
            }
            else
            {
                Cv2.CvtColor(frame, converted, ColorConversionCodes.BGRA2BGR);
                src = converted;
            }

            var byteLength = checked(src.Rows * src.Cols * src.ElemSize());
            var buffer = new byte[byteLength];
            Marshal.Copy(src.Data, buffer, 0, byteLength);

            lock (_frameLock)
            {
                _latestBgr = buffer;
                _latestWidth = src.Cols;
                _latestHeight = src.Rows;
                _hasNewFrame = true;
            }
        }
    }

    private void CreateTexture()
    {
        _textureId = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _textureId);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_captureThread.IsAlive)
        {
            _captureThread.Join(300);
        }

        _capture.Release();
        _capture.Dispose();

        if (_textureId != 0)
        {
            GL.DeleteTexture(_textureId);
            _textureId = 0;
        }

        IsRunning = false;
    }
}
