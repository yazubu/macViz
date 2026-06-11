using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using ManagedBass;

namespace macViz;

public sealed class AudioSpectrumAnalyzer : IDisposable
{
    private const int SampleRate = 44_100;
    private const int Channels = 1;
    private const int RingBufferSize = 65_536;
    private const int FftSize = 1024;
    private const int SpectrumBins = 512;

    private readonly float[] _ringBuffer = new float[RingBufferSize];
    private readonly float[] _copyBuffer = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly Complex[] _fftBuffer = new Complex[FftSize];
    private readonly ConcurrentQueue<float[]> _spectrumQueue = new();
    private float[] _recordScratch = Array.Empty<float>();

    private int _writeIndex;
    private int _recordHandle;
    private bool _recordingInitialized;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _fftTask;

    private long _recordCallbackCount;

    public AudioSpectrumAnalyzer()
    {
        for (var i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5f * (1f - MathF.Cos((2f * MathF.PI * i) / (FftSize - 1)));
        }

        if (!Bass.RecordInit(-1))
        {
            throw new InvalidOperationException($"BASS_RecordInit failed: {Bass.LastError}");
        }

        _recordingInitialized = true;

        ConfigureRecordingInput();

        _recordHandle = Bass.RecordStart(SampleRate, Channels, BassFlags.Float, 10, RecordCallback, IntPtr.Zero);
        if (_recordHandle == 0)
        {
            throw new InvalidOperationException($"BASS_RecordStart failed: {Bass.LastError}");
        }

        _fftTask = Task.Run(FftWorkerLoop);
    }

    public long RecordCallbackCount => Interlocked.Read(ref _recordCallbackCount);

    public bool TryDequeueLatest(out float[] latest)
    {
        latest = Array.Empty<float>();
        var hasAny = false;

        while (_spectrumQueue.TryDequeue(out var spectrum))
        {
            latest = spectrum;
            hasAny = true;
        }

        return hasAny;
    }

    private bool RecordCallback(int handle, IntPtr buffer, int length, IntPtr user)
    {
        if (length <= 0)
        {
            return false;
        }

        var sampleCount = length / sizeof(float);
        if (_recordScratch.Length < sampleCount)
        {
            _recordScratch = new float[sampleCount];
        }

        Marshal.Copy(buffer, _recordScratch, 0, sampleCount);

        var idx = Volatile.Read(ref _writeIndex);
        for (var i = 0; i < sampleCount; i++)
        {
            _ringBuffer[idx] = _recordScratch[i];
            idx = (idx + 1) % RingBufferSize;
        }

        Volatile.Write(ref _writeIndex, idx);
        Interlocked.Increment(ref _recordCallbackCount);
        return true;
    }

    private void ConfigureRecordingInput()
    {
        for (var input = 0; ; input++)
        {
            var name = Bass.RecordGetInputName(input);
            if (name is null)
            {
                break;
            }

            Bass.RecordSetInput(input, InputFlags.On, 1f);
        }
    }

    private async Task FftWorkerLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            RunFft();

            try
            {
                await Task.Delay(16, _cts.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private void RunFft()
    {
        var write = Volatile.Read(ref _writeIndex);
        var start = write - FftSize;
        if (start < 0)
        {
            start += RingBufferSize;
        }

        var idx = start;
        for (var i = 0; i < FftSize; i++)
        {
            _copyBuffer[i] = _ringBuffer[idx] * _window[i];
            idx = (idx + 1) % RingBufferSize;
        }

        for (var i = 0; i < FftSize; i++)
        {
            _fftBuffer[i] = new Complex(_copyBuffer[i], 0.0);
        }

        FftInPlace(_fftBuffer);

        var magnitudes = new float[SpectrumBins];
        for (var i = 0; i < SpectrumBins; i++)
        {
            var c = _fftBuffer[i];
            var mag = MathF.Sqrt((float)(c.Real * c.Real + c.Imaginary * c.Imaginary));
            magnitudes[i] = 20f * MathF.Log10(MathF.Max(mag, 1e-7f));
        }

        _spectrumQueue.Enqueue(magnitudes);
        while (_spectrumQueue.Count > 3)
        {
            _spectrumQueue.TryDequeue(out _);
        }
    }

    private static void FftInPlace(Complex[] buffer)
    {
        var n = buffer.Length;
        var bits = (int)Math.Log2(n);

        for (var i = 0; i < n; i++)
        {
            var j = ReverseBits(i, bits);
            if (j > i)
            {
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2.0 * Math.PI / len;
            var wLen = new Complex(Math.Cos(ang), Math.Sin(ang));

            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var j = 0; j < len / 2; j++)
                {
                    var u = buffer[i + j];
                    var v = buffer[i + j + len / 2] * w;

                    buffer[i + j] = u + v;
                    buffer[i + j + len / 2] = u - v;

                    w *= wLen;
                }
            }
        }
    }

    private static int ReverseBits(int value, int bits)
    {
        var reversed = 0;
        for (var i = 0; i < bits; i++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _fftTask.Wait(200);
        }
        catch
        {
            // ignored
        }

        if (_recordHandle != 0)
        {
            Bass.ChannelStop(_recordHandle);
            _recordHandle = 0;
        }

        if (_recordingInitialized)
        {
            Bass.RecordFree();
            _recordingInitialized = false;
        }
    }
}
