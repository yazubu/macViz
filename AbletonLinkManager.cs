using System.Runtime.InteropServices;

namespace macViz;

public sealed class AbletonLinkManager : IBeatClock, IDisposable
{
    private IntPtr _linkHandle;
    private IntPtr _sessionStateHandle;

    public bool IsAvailable { get; private set; }
    public bool IsEnabled { get; private set; }
    public int NumPeers { get; private set; }

    public float Bpm { get; private set; } = 120f;
    public long BeatNumber { get; private set; }
    public float BeatPhase { get; private set; }

    public void Initialize(float initialBpm)
    {
        if (IsAvailable) return;

        try
        {
            _linkHandle = Native.abl_link_create(initialBpm);
            _sessionStateHandle = Native.abl_link_create_session_state();

            if (_linkHandle == IntPtr.Zero || _sessionStateHandle == IntPtr.Zero)
            {
                CleanupHandles();
                IsAvailable = false;
                return;
            }

            IsAvailable = true;
            Bpm = initialBpm;
            Enable(true);
        }
        catch (DllNotFoundException)
        {
            IsAvailable = false;
        }
        catch (EntryPointNotFoundException)
        {
            IsAvailable = false;
        }
        catch (BadImageFormatException)
        {
            IsAvailable = false;
        }
    }

    public void Enable(bool enabled)
    {
        if (!IsAvailable || _linkHandle == IntPtr.Zero)
        {
            IsEnabled = false;
            return;
        }

        try
        {
            Native.abl_link_enable(_linkHandle, enabled ? 1 : 0);
            IsEnabled = Native.abl_link_is_enabled(_linkHandle) != 0;
        }
        catch
        {
            IsEnabled = false;
        }
    }

    public void SetTempo(float bpm)
    {
        Bpm = bpm;

        if (!IsAvailable || _linkHandle == IntPtr.Zero || _sessionStateHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var nowMicros = Native.abl_link_clock_micros(_linkHandle);
            Native.abl_link_capture_app_session_state(_linkHandle, _sessionStateHandle);
            Native.abl_link_set_tempo(_sessionStateHandle, bpm, nowMicros);
            Native.abl_link_commit_app_session_state(_linkHandle, _sessionStateHandle);
        }
        catch
        {
            // fallback silently
        }
    }

    public void Update(float deltaTime)
    {
        if (!IsAvailable || !IsEnabled || _linkHandle == IntPtr.Zero || _sessionStateHandle == IntPtr.Zero)
        {
            NumPeers = 0;
            return;
        }

        try
        {
            NumPeers = Native.abl_link_num_peers(_linkHandle);

            var nowMicros = Native.abl_link_clock_micros(_linkHandle);
            Native.abl_link_capture_app_session_state(_linkHandle, _sessionStateHandle);

            Bpm = (float)Native.abl_link_tempo(_sessionStateHandle);

            var beat = Native.abl_link_beat_at_time(_sessionStateHandle, nowMicros, 1.0);
            var phase = Native.abl_link_phase_at_time(_sessionStateHandle, nowMicros, 1.0);

            var beatFloor = Math.Floor(beat);
            BeatNumber = (long)beatFloor;
            BeatPhase = (float)phase;
        }
        catch
        {
            NumPeers = 0;
        }
    }

    public void Dispose()
    {
        CleanupHandles();
    }

    private void CleanupHandles()
    {
        if (_sessionStateHandle != IntPtr.Zero)
        {
            try
            {
                Native.abl_link_destroy_session_state(_sessionStateHandle);
            }
            catch
            {
                // ignored
            }

            _sessionStateHandle = IntPtr.Zero;
        }

        if (_linkHandle != IntPtr.Zero)
        {
            try
            {
                Native.abl_link_destroy(_linkHandle);
            }
            catch
            {
                // ignored
            }

            _linkHandle = IntPtr.Zero;
        }
    }

    private static class Native
    {
        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr abl_link_create(double bpm);

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void abl_link_destroy(IntPtr link);

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void abl_link_enable(IntPtr link, int enabled);

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int abl_link_is_enabled(IntPtr link);

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int abl_link_num_peers(IntPtr link);

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern long abl_link_clock_micros(IntPtr link);

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr abl_link_create_session_state();

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void abl_link_destroy_session_state(IntPtr sessionState);

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void abl_link_capture_app_session_state(IntPtr link, IntPtr sessionState);

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void abl_link_commit_app_session_state(IntPtr link, IntPtr sessionState);

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern double abl_link_tempo(IntPtr sessionState);

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void abl_link_set_tempo(IntPtr sessionState, double bpm, long atTimeMicros);

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern double abl_link_beat_at_time(IntPtr sessionState, long timeMicros, double quantum);

        [DllImport("abl_link", CallingConvention = CallingConvention.Cdecl)]
        internal static extern double abl_link_phase_at_time(IntPtr sessionState, long timeMicros, double quantum);
    }
}
