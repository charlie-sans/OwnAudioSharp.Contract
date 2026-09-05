using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ObjektRT.Core.Attributes;
using OwnaudioNET;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;

namespace OwnAudioSharp;

/// <summary>
/// A C# bridge host exposing OwnaudioNET 4.0.6 to Contract code as a set of
/// <c>&lt;ShadowBinding&gt;</c> targets: <c>OwnAudio</c> (engine lifecycle and
/// config), plus <c>FileSource</c> and <c>AudioMixer</c> (opaque object-handle
/// widgets). Across the boundary these are reached from Contract as
/// <c>import OwnAudioSharp;</c>.
///
/// Audio objects cross the boundary as opaque CLR <c>object</c> handles (the
/// same mechanism the stdlib uses for <c>DateTime</c> / <c>ManagedPtr</c>):
/// factory statics here return the real <c>FileSource</c> / <c>AudioMixer</c>
/// instance, every other static takes the handle back and <see cref="Unwrap"/>s
/// it on the way in. The Contract side never sees the CLR layout, only the
/// handle — no integer-ID registries are needed.
///
/// Audio processing uses <c>Span&lt;float&gt;</c>, which cannot cross the
/// Contract boundary, so all rendering stays C#-side; only scalar reads like
/// Position/Duration/Volume, or a raw native pointer for zero-copy output via
/// <c>Send</c>, cross.
///
/// Lifecycle (mirrors <c>OwnaudioNet</c>): Initialize → Start → … → Shutdown.
/// The engine may be a real device or a software "mock" engine so a demo can
/// run with no sound card or audio file present.
/// </summary>
[ClassBinding("OwnAudio")]
public static class OwnAudioBridge
{
    [ModuleInitializer]
    internal static void InitializeNativeLibrary() => EnsureNativeLibLoaded();

    // OwnaudioNET owns the DllImportResolver for its Rust FFI and resolves
    // "ownaudio_ffi" through the managed native search path (appbase first),
    // which under Assembly.LoadFrom is the host CLI's folder, not ours. But on
    // Windows the loader returns an already-loaded module by base name, so
    // pre-loading the native lib from OUR directory guarantees every later
    // LoadLibrary("ownaudio_ffi") (including ownaudio's own resolver) gets the
    // same module. On Linux/macOS the resolver also falls back to the LoadFrom
    // directory, so the staged file is found either way. Called from the
    // module initializer and again defensively.
    internal static void EnsureNativeLibLoaded()
    {
        if (s_nativeLibLoaded) return;
        var dir = Path.GetDirectoryName(typeof(OwnAudioBridge).Assembly.Location);
        var path = dir != null ? Path.Combine(dir, NativeLibFileName) : null;
        if (path != null && File.Exists(path))
        {
            try { NativeLibrary.Load(path); s_nativeLibLoaded = true; }
            catch { /* surfaced later by Ownaudio if not critical */ }
        }
    }
    private static bool s_nativeLibLoaded;

    // The native FFI file name differs per platform and is staged into the
    // bridge output by the csproj (runtimes/<rid>/native/ownaudio_ffi.dll,
    // libownaudio_ffi.so, libownaudio_ffi.dylib).
    private static string NativeLibFileName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ownaudio_ffi.dll" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libownaudio_ffi.dylib" :
        "libownaudio_ffi.so";

    // ── Lifecycle ──────────────────────────────────────────────────────

    /// <summary>True once the engine has been initialized (with or without mock).</summary>
    public static bool IsInitialized() => OwnaudioNet.IsInitialized;

    /// <summary>True when the device is actively running.</summary>
    public static bool IsRunning() => OwnaudioNet.IsRunning;

    /// <summary>
    /// Initializes the OwnaudioNET engine. When <paramref name="mock"/> is
    /// true a software "mock" engine is used so no physical sound card is
    /// required — useful for CI demos. Returns true on success.
    /// </summary>
    public static bool Initialize(bool mock, int sampleRate, int channels, int bufferSize)
    {
        lock (s_sync)
        {
            EnsureNativeLibLoaded();
            try
            {
                var config = new Ownaudio.Core.AudioConfig
                {
                    SampleRate = Math.Max(8000, sampleRate),
                    Channels = Math.Max(1, Math.Min(channels, 32)),
                    BufferSize = Math.Max(64, bufferSize),
                };
                OwnaudioNet.Initialize(config, mock, 1, Logger.Log.Level.Disabled);
                return OwnaudioNet.IsInitialized;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Initializes with a real (non-mock) engine.</summary>
    public static bool InitializeDefault(int sampleRate, int channels, int bufferSize)
        => Initialize(false, sampleRate, channels, bufferSize);

    /// <summary>Starts the audio engine. No-op when already running.</summary>
    public static void Start()
    {
        if (OwnaudioNet.IsInitialized && !OwnaudioNet.IsRunning)
            OwnaudioNet.Start();
    }

    /// <summary>Stops the audio engine (keeps it initialized).</summary>
    public static void Stop()
    {
        if (OwnaudioNet.IsRunning)
            OwnaudioNet.Stop();
    }

    /// <summary>Shuts the engine down and disposes it.</summary>
    public static void Shutdown()
    {
        lock (s_sync)
        {
            if (OwnaudioNet.IsInitialized)
                OwnaudioNet.Shutdown();
        }
    }

    // ── Config ──────────────────────────────────────────────────────────

    /// <summary>Reports the engine's configured sample rate, or 0 pre-init.</summary>
    public static int SampleRate() => OwnaudioNet.Engine?.Config.SampleRate ?? 0;

    /// <summary>Reports the engine's configured output channel count, or 0 pre-init.</summary>
    public static int Channels() => OwnaudioNet.Engine?.Config.Channels ?? 0;

    /// <summary>Reports the engine's frames per buffer, or 0 pre-init.</summary>
    public static int BufferSize() => OwnaudioNet.Engine?.FramesPerBuffer ?? 0;

    /// <summary>Count of enumerable output devices (mock engine returns 1).</summary>
    public static int OutputDeviceCount()
        => OwnaudioNet.Engine is { } e ? Safe(() => e.GetOutputDevices().Count) : 0;

    /// <summary>Name of the <paramref name="index"/>-th output device, or
    /// empty when out of range.</summary>
    public static string OutputDeviceName(int index)
        => OwnaudioNet.Engine is { } e ? Safe(() =>
        {
            var devs = e.GetOutputDevices();
            return index >= 0 && index < devs.Count ? devs[index]?.Name ?? "" : "";
        }) : "";

    /// <summary>Id (device GUID) of the <paramref name="index"/>-th output
    /// device, or empty when out of range.</summary>
    public static string OutputDeviceId(int index)
        => OwnaudioNet.Engine is { } e ? Safe(() =>
        {
            var devs = e.GetOutputDevices();
            return index >= 0 && index < devs.Count ? devs[index]?.DeviceId ?? "" : "";
        }) : "";

    /// <summary>True when the <paramref name="index"/>-th output device is the
    /// system default (the one <c>InitializeDefault</c> uses when no explicit
    /// device is requested).</summary>
    public static bool OutputDeviceIsDefault(int index)
        => OwnaudioNet.Engine is { } e ? Safe(() =>
        {
            var devs = e.GetOutputDevices();
            return index >= 0 && index < devs.Count && devs[index]!.IsDefault;
        }) : false;

    /// <summary>Index of the system default output device, or -1 when the
    /// engine cannot find one (pre-init, or the mock which reports none).</summary>
    public static int DefaultOutputDeviceIndex()
        => OwnaudioNet.Engine is { } e ? Safe(() =>
        {
            var devs = e.GetOutputDevices();
            for (var i = 0; i < devs.Count; i++)
                if (devs[i].IsDefault) return i;
            return -1;
        }) : -1;

    // ── Programmatic output ─────────────────────────────────────────────

    /// <summary>
    /// Sends <paramref name="sampleCount"/> interleaved <c>float</c> samples
    /// located at the raw native <paramref name="address"/> (from a Contract
    /// <c>ManagedPtr&lt;float&gt;</c>'s <c>&amp;</c> / <c>Address()</c>) straight
    /// to the output device, zero-copy via <c>OwnaudioNet.Send</c>. The address
    /// must stay valid for the call (do not <c>Free()</c> the buffer first) and
    /// hold at least <paramref name="sampleCount"/> <c>float</c>s. Sample count
    /// is total floats (frames × channels). Returns false if not initialized or
    /// given a non-positive count.
    /// </summary>
    public static unsafe bool SendSamples(long address, int sampleCount)
    {
        if (!OwnaudioNet.IsInitialized || address == 0 || sampleCount <= 0)
            return false;
        var span = new ReadOnlySpan<float>((void*)new IntPtr(address).ToPointer(), sampleCount);
        OwnaudioNet.Send(span);
        return true;
    }

    /// <summary>Routes output to the named device (e.g. the system default).
    /// Returns false when the engine is not initialized or the device is
    /// unknown. The system default output device is used when never called.</summary>
    public static bool SetOutputDeviceByName(string deviceName)
        => OwnaudioNet.Engine is { } e && !string.IsNullOrEmpty(deviceName)
            ? Safe(() => e.SetOutputDeviceByName(deviceName)) : false;

    /// <summary>Routes output to the <paramref name="index"/>-th output device.
    /// Returns false pre-init, while the engine is running, or for an
    /// out-of-range index. The system default output device is used when never
    /// called.</summary>
    public static bool SetOutputDeviceByIndex(int index)
    {
        if (OwnaudioNet.Engine is not { } e || OwnaudioNet.IsRunning)
            return false;
        return Safe(() => e.UnderlyingEngine.SetOutputDeviceByIndex(index) == 0);
    }

    /// <summary>Samples currently queued in the engine's output ring buffer
    /// (the fill level, in interleaved floats). 0 pre-init.</summary>
    public static int OutputBufferAvailable()
        => OwnaudioNet.Engine is { } e ? e.OutputBufferAvailable : 0;

    /// <summary>Total capacity of the engine's output ring buffer in
    /// interleaved floats = OutputRingFrames × channels. 0 pre-init.</summary>
    public static int OutputRingSamples()
        => OwnaudioNet.Engine is { } e ? e.OutputRingFrames * Channels() : 0;

    /// <summary>Samples of free space left in the output ring buffer
    /// ( &gt;= 0; on a mock/unreal device where fill reporting is absent this
    /// falls back to the full ring size so a producer never stalls).</summary>
    public static int FreeOutputSamples()
    {
        if (OwnaudioNet.Engine is not { } e) return 0;
        var fill = e.OutputBufferAvailable;
        // If the engine reports no meaningful fill (mock), treat as empty so
        // the producer is never falsely stalled.
        if (fill <= 0) return OutputRingSamples();
        return Math.Max(0, OutputRingSamples() - fill);
    }

    /// <summary>Cumulative output underruns since engine start (0 pre-init).</summary>
    public static long TotalUnderruns()
        => OwnaudioNet.Engine is { } e ? e.TotalUnderruns : 0;

    /// <summary>Drops everything currently queued in the output ring buffer.</summary>
    public static void ClearOutputBuffer()
    {
        if (OwnaudioNet.Engine is { } e)
        {
            try { e.ClearOutputBuffer(); } catch { }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static readonly object s_sync = new();

    private static T Safe<T>(Func<T> fn)
    {
        try { return fn(); }
        catch { return default!; }
    }
}

/// <summary>
/// Factory and opaque-handle operations for a single audio track. The
/// <c>&lt;ShadowBinding("FileSource")&gt;</c> Contract wrapper holds the boxed
/// <see cref="FileSource"/> as an opaque <c>object</c> handle and delegates
/// every operation here by passing the handle back.
/// </summary>
[ClassBinding("FileSource")]
public static class FileSourceHost
{
    private static readonly object s_sync = new();

    private static FileSource Unwrap(object handle)
        => handle is FileSource fs ? fs : throw new InvalidOperationException("OwnAudio.FileSource: handle is not a FileSource");

    /// <summary>
    /// Creates a FileSource for <paramref name="path"/> and returns an opaque
    /// handle. Returns null when the file is missing / undecodable / not
    /// initialized.
    /// </summary>
    public static object Create(string path, int bufferSize)
    {
        lock (s_sync)
        {
            if (!OwnaudioNet.IsInitialized || string.IsNullOrEmpty(path))
                return null!;
            try
            {
                var config = OwnaudioNet.Engine!.Config;
                var buf = bufferSize > 0 ? bufferSize : config.BufferSize;
                return new FileSource(path, buf, config.SampleRate, config.Channels);
            }
            catch (Exception)
            {
                return null!;
            }
        }
    }

    /// <summary>Starts playback of the source. Returns false for a bad handle.</summary>
    public static bool Play(object handle) { Unwrap(handle).Play(); return true; }

    /// <summary>Pauses playback.</summary>
    public static bool Pause(object handle) { Unwrap(handle).Pause(); return true; }

    /// <summary>Stops playback (rewinds to the start).</summary>
    public static bool Stop(object handle) { Unwrap(handle).Stop(); return true; }

    /// <summary>Seeks the source to <paramref name="positionInSeconds"/>. Returns
    /// false when the seek fails.</summary>
    public static bool Seek(object handle, double positionInSeconds) => Unwrap(handle).Seek(positionInSeconds);

    /// <summary>Current playback position in seconds.</summary>
    public static double Position(object handle) => Unwrap(handle).Position;

    /// <summary>Track duration in seconds.</summary>
    public static double Duration(object handle) => Unwrap(handle).Duration;

    /// <summary>True when the source has reached end of stream.</summary>
    public static bool IsEndOfStream(object handle) => Unwrap(handle).IsEndOfStream;

    /// <summary>Loops the source when true (defaults to false).</summary>
    public static bool SetLoop(object handle, bool loop) { Unwrap(handle).Loop = loop; return true; }

    /// <summary>Returns the source's Volume (0..1).</summary>
    public static float Volume(object handle) => Unwrap(handle).Volume;

    /// <summary>Sets the source volume (0..1); clamps into range.</summary>
    public static bool SetVolume(object handle, float volume)
    {
        Unwrap(handle).Volume = Math.Clamp(volume, 0f, 1f);
        return true;
    }

    /// <summary>Disposes the source. The handle is unusable afterwards.</summary>
    public static void Dispose(object handle)
    {
        try { Unwrap(handle).Dispose(); } catch { }
    }
}

/// <summary>
/// Factory and opaque-handle operations for an <see cref="AudioMixer"/> on the
/// shared engine. The <c>&lt;ShadowBinding("AudioMixer")&gt;</c> Contract wrapper
/// holds the boxed <see cref="AudioMixer"/> as an opaque <c>object</c> handle and
/// delegates every operation here, passing child <c>FileSource</c> handles through
/// when wiring sources in.
/// </summary>
[ClassBinding("AudioMixer")]
public static class AudioMixerHost
{
    private static readonly object s_sync = new();

    private static AudioMixer Unwrap(object handle)
        => handle is AudioMixer m ? m : throw new InvalidOperationException("OwnAudio.AudioMixer: handle is not an AudioMixer");

    /// <summary>
    /// Creates an AudioMixer on the shared engine and returns an opaque handle.
    /// Returns null when not initialized. Mixers are not started automatically.
    /// </summary>
    public static object Create(int bufferSize)
    {
        lock (s_sync)
        {
            if (!OwnaudioNet.IsInitialized || OwnaudioNet.Engine is not { } engine)
                return null!;
            try
            {
                var buf = bufferSize > 0 ? bufferSize : engine.Config.BufferSize;
                return AudioMixer.Create(engine, buf);
            }
            catch (Exception)
            {
                return null!;
            }
        }
    }

    /// <summary>Starts the mixer's render loop. Returns false for a bad handle.</summary>
    public static bool Start(object handle) { Unwrap(handle).Start(); return true; }

    /// <summary>Stops the mixer.</summary>
    public static bool Stop(object handle) { Unwrap(handle).Stop(); return true; }

    /// <summary>Adds a source to the mixer (also starts it). Returns false when
    /// the add fails or the source is already present.</summary>
    public static bool AddSource(object mixerHandle, object sourceHandle)
    {
        var source = UnwrapSource(sourceHandle);
        return Unwrap(mixerHandle).AddSource(source);
    }

    /// <summary>Removes a source from the mixer. Returns false when the source
    /// is not wired into the mixer.</summary>
    public static bool RemoveSource(object mixerHandle, object sourceHandle)
    {
        var source = UnwrapSource(sourceHandle);
        return Unwrap(mixerHandle).RemoveSource(source);
    }

    /// <summary>Number of sources currently wired into the mixer.</summary>
    public static int SourceCount(object handle) => Unwrap(handle).SourceCount;

    /// <summary>Override master gain across every mixed source (0..1+).</summary>
    public static float MasterVolume(object handle) => Unwrap(handle).MasterVolume;

    /// <summary>Sets the mixer master volume (clamped to 0..2).</summary>
    public static bool SetMasterVolume(object handle, float volume)
    {
        Unwrap(handle).MasterVolume = Math.Clamp(volume, 0f, 2f);
        return true;
    }

    /// <summary>Disposes the mixer. The handle is unusable afterwards.</summary>
    public static void Dispose(object handle)
    {
        try { Unwrap(handle).Dispose(); } catch { }
    }

    private static FileSource UnwrapSource(object handle)
        => handle is FileSource fs ? fs : throw new InvalidOperationException("OwnAudio.AudioMixer: source handle is not a FileSource");
}

