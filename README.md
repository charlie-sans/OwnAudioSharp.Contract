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
  OwnAudioSharp.Contract.csproj
src/
  OwnAudio.ct          The Contract surface (namespace OwnAudioSharp)
samples/
  main.ct              Self-test demo (runs against the software mock engine)
  contract.ctproj      Defines the ImportRoots for the sample
run.ps1                Builds the bridge, then runs the sample through ccl --bind
```

## Surface

- **`OwnAudio`** — engine lifecycle (`Initialize`, `InitializeDefault`, `Start`,
  `Stop`, `Shutdown`, `IsInitialized`, `IsRunning`), configuration
  (`SampleRate`, `Channels`, `BufferSize`), device listing/routing
  (`OutputDeviceCount`, `OutputDeviceName`, `SetOutputDeviceByName`), and
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

`FileSource` and `AudioMixer` are *shadow* contracts: `new` allocates a managed
object whose single instance field `handle` points at the CLR object. Because a
shadow's instance-field layout is part of the compiled module, the object
constructors are static factories that run a default ctor and then assign
`handle`.

## Building

The bridge project references `ObjektRT.Core` and the `OwnaudioNET 4.0.6`
package, and stages the Ownaudio libs into its output so `ccl --bind` can load
them at runtime.

```powershell
# Build bridge + run sample against the mock engine:
.\run.ps1
```

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

## Notes

- **Mock engine:** `FreeOutputSamples()` falls back to the full ring size on the
  mock engine, so a producer never falsely stalls even though the mock reports
  no fill.
- **Zero-copy send:** `Send` takes the native `Address()` of a `ManagedPtr<float>`
  as a `long` and a float count (`frames * channels`). No ManagedPtr import is
  required by this library.