Core Modules
1. AppController
Initialises all subsystems (audio, camera, graphics, Link, LFOs).

Runs the main loop (update → render → present).

Handles shutdown and resource cleanup.

2. AudioAnalyzer (ManagedBass)
Captures microphone input in real time.

Computes FFT spectrum (512–2048 bins) on a separate thread.

Exposes:

Amplitude envelope (RMS).

Frequency band magnitudes (e.g., bass, mid, treble).

Optional beat detection via transient analysis.

Thread‑safe buffer for spectrum data.

3. CameraInput (OpenCvSharp)
Enumerates available webcams.

Captures frames as Mat (BGR).

Converts to OpenGL texture (RGBA) using TexImage2D.

Runs at capture framerate (e.g., 30 fps) in background.

4. AbletonLinkManager
P/Invoke wrapper around Ableton Link Kit (C API).

Provides:

Current tempo (BPM).

Current beat (floating point, e.g., 4.2).

Phase within current beat (0.0–1.0).

Bar/beat position.

Callbacks for tempo/beat changes (optional).

Lock‑free reads of timeline state for real‑time threads.

5. LFOEngine
Manages a collection of Lfo instances.

Each Lfo has:

Waveform: Sine, Square, Sawtooth, PWM (pulse width), Random, Sample & Hold.

Sync mode: Free (Hz) or BeatSync (tempo division, e.g., 1/1, 1/2, 2/1).

Phase (radians or 0..1) and frequency.

Update (called every frame):

For Free: phase += deltaTime * frequency (Hz).

For BeatSync: uses AbletonLinkManager.BeatPhase multiplied by sync ratio.

Output value in [-1, 1].

Sample & Hold: uses a noise source, updates on beat or every N samples.

6. ParameterManager
Registry of all automatable parameters (Parameter<T> where T is usually float).

Each parameter has:

Current value (manual base value + modulation contribution).

Range (min, max).

List of active modulations (LfoAssignment with lfoId, scale, offset).

Modulation update:
final = clamp( base + (lfoOutput * scale), min, max )

UI can expose any parameter via the Automation Matrix (grid showing LFOs vs parameters).

7. VisualPluginManager
Manages loading and switching of IVisual plugins.

IVisual interface:

csharp
interface IVisual {
    string Name { get; }
    void Load(GL context);
    void Unload();
    void Render(float[] spectrum, Texture cameraTexture, double time);
    IReadOnlyList<ParameterDescriptor> GetParameters(); // for automation
}
Example visuals:

SpectrumBars2D – 2D bar chart reacting to frequency bins.

RotatingCube3D – 3D cube with colour modulated by LFOs.

CameraFilterGrayscale – takes camera input, applies custom shader (edge detection, colour mapping, etc.).

8. UI Layer (ImGui + ImPlot)
Main Preview Window: OpenGL viewport displaying current visual.

Control Panel:

Visual selector (drop‑down).

Spectrum analyser (ImPlot line chart).

Link status (BPM, beat indicator).

LFO Editor:

Add/remove LFOs.

Per LFO: waveform dropdown, frequency/sync rate, sync toggle.

Live waveform preview (ImPlot).

Automation Matrix:

Rows = automatable parameters from active visual.

Columns = LFOs.

Each cell: “Assign” button + scale slider + offset.

Shows current modulation depth visually.

Software Stack (Cross‑platform, .NET 9)
Component	Technology / Library	Rationale
Runtime	.NET 9 (LTS)	Cross‑platform support, high performance, modern C# features.
Windowing & Graphics	OpenTK 4.8.0	OpenGL bindings, window creation, input handling. Works on Win/macOS.
UI Overlay	ImGui.NET (Dear ImGui) + ImPlot.NET	Fast, immediate‑mode GUI perfect for real‑time parameter tweaking.
Audio Capture & FFT	ManagedBass (BASS audio library)	Cross‑platform (WASAPI/CoreAudio/ALSA), low latency, built‑in FFT.
Ableton Link	Native Ableton Link Kit 3.x + custom P/Invoke	Only official cross‑platform API. Wrap core functions for tempo/beat.
Webcam Input	OpenCvSharp4 + native OpenCV binaries	Works on Windows (DirectShow) and macOS (AVFoundation).
Math / Vectors	System.Numerics	Built‑in Vector3, Matrix4x4 for 3D visuals.
Dependency Injection	Microsoft.Extensions.DependencyInjection	Optional – clean module wiring.
Native Library Handling
BASS: Provide libbass.dll, libbass.dylib, libbass.so in output folder.

OpenCV: Use OpenCvSharp4.runtime.* NuGet packages – they bundle native binaries.

Ableton Link: Place libabl_link.dll (Win), libabl_link.dylib (macOS) – load via [DllImport] with platform‑specific paths.

Data Flow & Synchronisation
Audio → Visuals
AudioAnalyzer callback (every ~20 ms) writes FFT into a lock‑free ring buffer.

Main render loop reads latest spectrum buffer.

Visual’s Render method uses spectrum data to drive geometry/colour.

Ableton Link → LFOs
AbletonLinkManager polls Link timeline at each frame (e.g., using LinkGetBeatAtTime).

Beat‑synced LFOs compute phase:
phase = fmod(beat * syncRatio, 1.0)

syncRatio = targetBpm / linkBpm or simpler: speedMatch (1:1 → 1, 1:2 → 0.5, 2:1 → 2).

Waveform evaluation yields [-1,1] output.

ParameterManager applies modulation with scale/offset.

Camera Input → Visuals
CameraInput runs a background thread, captures frames, and uploads to a shared OpenGL texture ID.

Visuals that act as camera filters sample this texture inside their shaders.

Threading Model
Audio thread (BASS callback) – FFT only. No blocking.

Camera thread (OpenCV) – capture & texture upload.

Main thread (OpenTK/ImGui) – rendering, UI, LFO update, Link polling. All OpenGL calls here.

LFO Implementation Details
Waveform Generation
csharp
float EvaluateWaveform(Waveform type, float phase, float pwmWidth = 0.5f) {
    switch(type) {
        case Sine:      return MathF.Sin(phase * 2π);
        case Square:    return phase < 0.5f ? 1f : -1f;
        case Sawtooth:  return phase * 2f - 1f;
        case PWM:       return phase < pwmWidth ? 1f : -1f;
        case Random:    return Random.NextFloat(-1, 1); // new value per call?
        case SampleHold:return sampleAndHold(phase);    // updates at beat intervals
    }
}
Sample & Hold updates its value on beat boundaries (using Link beat) or on a fixed clock (e.g., 1/8 notes).

Beat‑sync timing
Use AbletonLinkManager.BeatPhase (0..1) and AbletonLinkManager.BeatNumber.

For a 1:1 LFO: phase = BeatPhase.

For 1:2 (one cycle per two beats): phase = (BeatNumber % 2) + BeatPhase / 2?
Simpler: phase = fmod(BeatNumber + BeatPhase, syncPeriod) where syncPeriod = beats per cycle (1 for 1:1, 2 for 1:2, 0.5 for 2:1).

Free‑running LFO
Use Stopwatch.GetTimestamp() for high‑resolution delta time.

Phase = (frequencyHz * elapsedSeconds) % 1.0.

Automation Matrix & Parameter Exposure
Parameter Registration
Each visual implements GetParameters() returning a list of ParameterDescriptor:

csharp
new ParameterDescriptor("Color Hue", 0f, 1f, 0.5f);
ParameterManager creates a ModulatableFloat that holds base value, min, max, and a list of modulations.

Matrix UI (ImGui Table)
Rows: parameters (colour, rotation speed, scale, etc.).

Columns: LFOs (LFO 1, LFO 2, …).

Each cell shows:

“Assign” toggle (or dropdown to select modulation amount).

Slider for scale (range 0..2).

Slider for offset (range -1..1).

Real‑time preview of resulting modulated value.

Sample Visual Implementation
2D Visual – Spectrum Bars
OpenGL immediate mode or shader‑based.

Each bar’s height = spectrum[i] * parameterManager.GetValue("Bar Amplitude").

Colour = hue modulated by LFO assigned to “Hue Offset”.

3D Visual – Rotating Particles
10k points in 3D space.

Rotation speed = LFO (sync to beat).

Size = bass amplitude.

Colour = spectrum centroid.

Camera Filter – Edge Detection
Takes webcam texture, uses Sobel shader.

Edge intensity = LFO (free‑running) to create pulsating effect.

Build & Deployment (Cross‑Platform)
Project Structure
text
VisualSynthesizer/
├── Program.cs (entry – boots AppController)
├── Core/
│   ├── AbletonLinkManager.cs
│   ├── AudioAnalyzer.cs
│   ├── CameraInput.cs
│   ├── LFOEngine.cs
│   └── ParameterManager.cs
├── Visuals/
│   ├── IVisual.cs
│   ├── SpectrumBars2D.cs
│   ├── RotatingCube3D.cs
│   └── CameraFilterGrayscale.cs
├── UI/
│   └── ImGuiUI.cs
├── NativeLibs/  (Win/macOS/Linux – copied to output)
└── VisualSynthesizer.csproj
Startup Checks
Verify microphone access (BASS_RecordInit).

Verify webcam availability (Cv2.VideoCapture).

Load Ableton Link native library (DllMap or manual path).

If Link is not found, fall back to internal metronome.

Performance Considerations
FFT size: 2048 bins @ 44.1 kHz → 21 ms latency, acceptable.

LFO update: Compute only once per frame (60 Hz), cheap.

Texture upload: Use TexSubImage2D and keep webcam frame at native resolution; scale in shader.

Multithreading: Use ConcurrentQueue for spectrum frames; for camera, use GL.Context sync or shared texture ID with fences.

ImGui draw calls: Batch as usual; avoid per‑frame allocations.

Conclusion
This architecture delivers a modular, real‑time visual synthesiser that satisfies all requirements:

Microphone spectrum analysis.

2D/3D generated visuals and camera‑filter visuals.

Ableton Link synchronisation for beat‑synced LFOs.

Full automation matrix with free‑running or sync LFOs (sine, square, saw, PWM, random, S&H).

Cross‑platform .NET Core (Windows + macOS) with minimal native dependencies.