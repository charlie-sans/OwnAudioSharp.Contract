# OwnAudioSharp.Contract

A reusable [Contract](https://github.com/fy-nite/Contract) library surface over the
`OwnaudioNET 4.0.6` .NET audio engine, importable by **namespace**:

```contract
import OwnAudioSharp;

if (!OwnAudio.Initialize(true, 48000, 2, 512)) { /* ... */ }
```

The declared namespace `OwnAudioSharp` resolves this library's source
(`src/OwnAudio.ct`) no matter what the file is named, because the compiler's
content-based import matches on the declared namespace rather than the file
path. Consumers add this directory as an import root.

## Layout

```
bridge/               C# host of the CLR bindings (OwnAudioSharp.Contract.dll)
  OwnAudioBridge.cs        [ClassBinding] hosts: OwnAudio, FileSource, AudioMixer
  ChipSynthBridge.cs       [ClassBinding("Chip")] ChipSynth + Wave + ChipBuffer
  OwnAudioSharp.Contract.csproj
src/
  OwnAudio.ct          The Contract surface (namespace OwnAudioSharp)
  Chip.ct              The Contract surface for the chip engine (namespace OwnAudioSharp)
samples/
  main.ct              Self-test demo (runs against the software mock engine)
  contract.ctproj      Defines the ImportRoots for the sample
run.ps1                Builds the bridge, then runs the sample through ccl --bind
```

## Surface

- **`OwnAudio`** — engine lifecycle (`Initialize`, `InitializeDefault`, `Start`,
  `Stop`, `Shutdown`, `IsInitialized`, `IsRunning`), configuration
  (`SampleRate`, `Channels`, `BufferSize`), device listing/routing
  (`OutputDeviceCount`, `OutputDeviceName`, `OutputDeviceId`,
  `OutputDeviceIsDefault`, `DefaultOutputDeviceIndex`, `SetOutputDeviceByName`,
  `SetOutputDeviceByIndex`), and
  programmatic output: zero-copy `Send(address, sampleCount)` plus the output
  ring gauges (`FreeOutputSamples`, `OutputRingSamples`, `OutputBufferAvailable`,
  `TotalUnderruns`, `ClearOutputBuffer`).
- **`FileSource`** — one track; an opaque handle to a CLR `FileSource`.
  `FileSource.Open(path, bufferSize)` returns a track (`IsValid()` is false when
  the file is missing or the engine is not initialized). Play/pause/stop/seek/
  position/duration/loop/volume.
- **`AudioMixer`** — a mixer of tracks on the shared engine.
  `AudioMixer.Open(bufferSize)` returns a mixer, not started by default.
  Add/remove sources, start/stop, master volume.
- **`Chip`** — a reusable chiptune synth engine (waveforms, tones, melodies,
  chords, slides, MIDI playback) rendered into `ManagedPtr<float>` output
  buffers and streamed to the engine. See `samples/main.ct` and `ContraChip`.
  Supports **MIDI playback from C#**: `Chip.PlayMidiFile(path, wave, volume, speed)`
  loads the file with the Ownaudio MIDI parser inside the bridge and streams it.

`FileSource` and `AudioMixer` are *shadow* contracts: `new` allocates a managed
object whose single instance field `handle` points at the CLR object. Because a
shadow's instance-field layout is part of the compiled module, the object
constructors are static factories that run a default ctor and then assign
`handle`.

## Building

The bridge project references `ObjektRT.Core` and the `OwnaudioNET 4.0.6`
package. On build it stages the Ownaudio managed libs **plus** the
platform-correct native FFI (`ownaudio_ffi.dll`, `libownaudio_ffi.so` or
`libownaudio_ffi.dylib`, selected from the build host's OS + architecture) into
its output so `ccl --bind` can load them at runtime. Build with
`-r <rid>` (e.g. `win-x64`, `linux-x64`, `osx-arm64`) to force a specific
platform.

```powershell
# Build bridge + run sample against the mock engine:
.\run.ps1
# ...or against the system DEFAULT output device (real sound):
.\run.ps1 -RealDevice
```

The runner is PowerShell and is cross-platform — run it on Linux/macOS with
`pwsh ./run.ps1` (the bridge builds and the demo runs the same way; it has no
hardcoded drive paths).

## Default device & device indexes

`InitializeDefault(sampleRate, channels, bufferSize)` opens the system **default
output device** — pass a device id through `AudioConfig.OutputDeviceId` when you
need something specific. At runtime you can also route with an **index**:

```contract
var count: int = OwnAudio.OutputDeviceCount();
var def: int   = OwnAudio.DefaultOutputDeviceIndex();   // -1 when none
if (def >= 0) { OwnAudio.SetOutputDeviceByIndex(def); } // stop engine first
OwnAudio.Start();
```

`OutputDeviceName(i)` / `OutputDeviceId(i)` / `OutputDeviceIsDefault(i)` let a
producer print an index and mark the default while the engine runs.
`SetOutputDeviceByName(name)` and `SetOutputDeviceByIndex(i)` switch at runtime
but only while the engine is **stopped**.

## Using it from a consumer

```powershell
ccl --bind bridge\bin\Debug\net10.0\OwnAudioSharp.Contract.dll src\main.ct
```

and in `contract.ctproj` add the library's source dir as an import root so the
namespace import can find it:

```json
ImportRoots: ["../OwnAudioSharp.Contract/src", "../ContractStdlib/src"]
```

Streaming is a consumer concern: this library provides `Send(address, count)`,
`FreeOutputSamples()`, `TotalUnderruns()` and `ClearOutputBuffer()` so you can
fill a `ManagedPtr<float>` and push it out in your own loop. See
`samples/main.ct` for a full tone-streaming example and `ContraChip` for a
real-device consumer.

## Using the chip engine from C#

The escaping shell is a real .NET assembly, so C# consumers get the exact same
engine as Contract consumers. `ChipSynth` is a `public static` class and `Wave`
is a public enum in the `OwnAudioSharp` namespace — reference
`OwnAudioSharp.Contract.dll` (with the Ownaudio + Ownaudio Midi libs staged next
to it) and call it directly:

```csharp
using OwnAudioSharp;

OwnAudioBridge.Initialize(true, 48000, 2, 1024);   // same engine the host uses
ChipSynth.PlayNote(72, Wave.Square, 0.3, 0.5);
ChipSynth.PlayNoteDuty(64, Wave.Pulse, 0.3, 0.5, 0.25);
ChipSynth.PlayTone(freq: 523.25, Wave.Saw, seconds: 0.5, volume: 0.4, duty: 0.5);

int[] chord = { 60, 64, 67 };
ChipSynth.PlayChord(chord, Wave.Square, 0.8, 0.4);

int[] arp = { 60, 64, 67, 72, 67, 64 };
ChipSynth.PlayMelody(arp, noteLen: 0.12, Wave.Pulse, 0.5, gap: 0.02);

ChipSynth.PlaySlide(fromMidi: 80, toMidi: 40, Wave.Saw, seconds: 0.3, volume: 0.35);
bool ok = ChipSynth.PlayMidiFile("assets/song.mid", Wave.Square, 0.4, 0.5);
```

The host-facing overloads take `int` wave selectors (the VM passes Int32);
the `Wave` overloads above are the friendly C# entry points. Low-level
rendering (`RenderVoice`/`RenderChord`) returns a `ChipBuffer` wrapping an
unmanaged native buffer (`Address`, `Length`, `Free`), and the built-in
streamer (`SpawnStream`/`PlaySilence`) owns and frees its buffers on a
background thread.

## Notes

- **Mock engine:** `FreeOutputSamples()` falls back to the full ring size on the
  mock engine, so a producer never falsely stalls even though the mock reports
  no fill.
- **Device rerouting is best-effort.** `SetOutputDeviceByName`/`SetOutputDeviceByIndex`
  re-open the output stream on the chosen device; some hosts refuse a specific
  buffer size (e.g. an ALSA card that only accepts a fixed buffer), in which
  case they return `false`. `InitializeDefault` already opens the system default
  device, so on such hosts a producer keeps streaming over the device opened at
  init.
- **Zero-copy send:** `Send` takes the native `Address()` of a `ManagedPtr<float>`
  as a `long` and a float count (`frames * channels`). No ManagedPtr import is
  required by this library.