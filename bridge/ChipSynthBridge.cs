using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using ObjektRT.Core.Attributes;
using OwnAudio.Midi.File;

namespace OwnAudioSharp;

/// <summary>
/// Waveform selectors for <see cref="ChipSynth"/>. Names match the Contract
/// <c>Wave</c> enum in the library surface so the numeric values line up
/// 1:1 across the C# engine and the <c>.ct</c> shadow.
/// </summary>
public enum Wave
{
    /// <summary>Smooth periodic wave. Pure tone, no harmonics.</summary>
    Sine = 0,

    /// <summary>Classic 50% duty cycle square wave. Hollow, retro.</summary>
    Square = 1,

    /// <summary>Linear ramp from -1 to +1. Bright, buzzy harmonics.</summary>
    Saw = 2,

    /// <summary>Linear triangle. Mellow, flute-like, fewer harmonics than saw.</summary>
    Triangle = 3,

    /// <summary>White noise. Use for percussion, hats, snares.</summary>
    Noise = 4,

    /// <summary>Variable duty cycle square wave. Duty 0.05..0.95; thin at low, fat at 0.5.</summary>
    Pulse = 5,
}

/// <summary>
/// A pre-computed, interleaved stereo (or engine-channel-count) buffer of
/// <c>float</c> samples living in unmanaged memory. Created by
/// <see cref="ChipSynth.RenderVoice"/> / <see cref="ChipSynth.RenderChord"/>,
/// streamed to the output ring by <see cref="ChipSynth.SpawnStream"/>, and
/// freed either by that streamer when playback finishes or explicitly via
/// <see cref="Free"/>. Freeing is idempotent.
/// </summary>
public sealed class ChipBuffer : IDisposable
{
    private IntPtr _mem;
    private readonly int _samples;
    private int _freed;

    /// <summary>Allocates an unmanaged buffer of <paramref name="sampleCount"/> floats.</summary>
    public ChipBuffer(int sampleCount)
    {
        if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        _mem = sampleCount == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(sampleCount * sizeof(float));
        _samples = sampleCount;
    }

    /// <summary>Raw address of the sample data, or 0 when empty/freed.</summary>
    public long Address => _mem.ToInt64();

    /// <summary>Total float samples the buffer can hold.</summary>
    public int Length => _samples;

    /// <summary>True once this buffer has been freed.</summary>
    public bool IsFreed => _freed != 0;

    /// <summary>Releases the unmanaged memory. Idempotent.</summary>
    public void Free()
    {
        if (Interlocked.Exchange(ref _freed, 1) == 0 && _mem != IntPtr.Zero)
        {
            try { Marshal.FreeHGlobal(_mem); } finally { _mem = IntPtr.Zero; }
        }
    }

    /// <summary>Releases the unmanaged memory (see <see cref="Free"/>).</summary>
    public void Dispose() => Free();
}

/// <summary>
/// A reusable chiptune synth engine over the shared OwnaudioNET engine. Every
/// <c>Play*</c> call pre-renders its sound into an unmanaged
/// <see cref="ChipBuffer"/> offline, then streams it to the output ring on a
/// background thread and returns immediately. Multiple voices overlap
/// naturally — the output ring mixes them.
///
/// The class is exposed two ways:
///  * to C# consumers directly (call <see cref="PlayNote(int, int, float, float)"/> etc. —
///    a <see cref="Wave"/> overload is provided for each waveform parameter), and
///  * to Contract consumers as the <c>&lt;ShadowBinding&gt;</c> target
///    <c>Chip</c> (its <c>.ct</c> forwarder passes every wave selector as an
///    <c>int</c>, hence the <c>int</c> overloads below).
///
/// Rendering uses <c>Span&lt;float&gt;</c>/raw pointers, which cannot cross the
/// Contract boundary, so the actual DSP stays in C# regardless of caller.
/// Driving the engine is a prerequisite: <c>OwnAudio.Initialize</c> (or the
/// equivalent from C#) before any <c>Play*</c>.
/// </summary>
[ClassBinding("Chip")]
public static class ChipSynth
{
    private static readonly Random s_rng = new();

    // ── Pure helpers (no audio) ─────────────────────────────────────────

    /// <summary>Convert a MIDI note number to frequency in Hz (A4 = 440, equal temperament).</summary>
    public static float MidiFreq(int midi)
    {
        var semis = midi - 69;
        var f = 440.0f;
        var ratio = 1.0594631f;
        if (semis >= 0)
            for (var i = 0; i < semis; i++) f *= ratio;
        else
            for (var i = 0; i > semis; i--) f /= ratio;
        return f;
    }

    private static readonly string[] s_noteNames =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>Return the note name for a MIDI number, e.g. 60 => "C4", 69 => "A4".</summary>
    public static string NoteName(int midi)
    {
        var pc = midi - (midi / 12) * 12;
        var oct = midi / 12 - 1;
        return s_noteNames[pc] + oct.ToString();
    }

    /// <summary>Return a human-readable name for a wave value.</summary>
    public static string WaveName(int wave) => wave switch
    {
        0 => nameof(Wave.Sine),
        1 => nameof(Wave.Square),
        2 => nameof(Wave.Saw),
        3 => nameof(Wave.Triangle),
        4 => nameof(Wave.Noise),
        5 => nameof(Wave.Pulse),
        _ => "Unknown",
    };

    /// <summary>Return a human-readable name for a <see cref="Wave"/>.</summary>
    public static string WaveName(Wave wave) => WaveName((int)wave);

    /// <summary>
    /// Generate one sample from a waveform at a given phase (0.0..1.0 = one cycle).
    /// Returns a value in -1.0..+1.0; <see cref="Wave.Noise"/> returns random noise.
    /// </summary>
    public static float Oscillate(int wave, float phase, float duty)
    {
        switch (wave)
        {
            case 0: return (float)Math.Sin(phase * 6.2832);
            case 1: return phase < 0.5f ? 1.0f : -1.0f;
            case 2: return phase * 2.0f - 1.0f;
            case 3: return phase < 0.5f ? phase * 4.0f - 1.0f : 3.0f - phase * 4.0f;
            case 4: return (float)(s_rng.Next(2000) - 1000) / 1000.0f;
        }
        var d = Math.Max(0.05f, Math.Min(0.95f, duty));
        return phase < d ? 1.0f : -1.0f;
    }

    /// <summary>Generate one sample from a <see cref="Wave"/> at a given phase.</summary>
    public static float Oscillate(Wave wave, float phase, float duty) => Oscillate((int)wave, phase, duty);

    /// <summary>Quantize a value to a given number of discrete steps (0 or fewer = no quantization).</summary>
    public static float Quantize(float s, int steps) => steps <= 0 ? s : (float)Math.Round(s * steps) / steps;

    /// <summary>Clamp a volume value to 0.0..1.0.</summary>
    public static float ClampVol(float v) => Math.Max(0.0f, Math.Min(1.0f, v));

    // ── Rendering (offline, no timing pressure) ─────────────────────────

    /// <summary>
    /// Render a full interleaved buffer for one voice into unmanaged memory.
    /// Applies fade-in/out (~5 ms), volume envelope, optional frequency sweep
    /// (when <paramref name="sweep"/> is true, sweeping <paramref name="freq"/>
    /// to <paramref name="freqEnd"/>) and optional bit crush. The caller owns
    /// the returned buffer: pass it to <see cref="SpawnStream(object, int)"/>, or
    /// free it yourself.
    /// </summary>
    public static unsafe ChipBuffer RenderVoice(float freq, int wave, int total, float volume, float duty, float freqEnd, float crush, bool sweep)
    {
        var sr = OwnAudioBridge.SampleRate();
        var channels = OwnAudioBridge.Channels();
        if (sr <= 0 || channels <= 0 || total <= 0) return new ChipBuffer(0);

        var srf = (float)sr;
        var buf = new ChipBuffer(total * channels);
        var p = (float*)new IntPtr(buf.Address).ToPointer();

        var phase = 0.0f;
        var fade = Math.Max(16, sr / 200);
        var crushSteps = (int)crush;
        var totalF = (float)total;
        var fadeF = (float)fade;

        for (var i = 0; i < total; i++)
        {
            var f = freq;
            if (sweep)
            {
                var t = (float)i / totalF;
                f = freq + (freqEnd - freq) * t;
            }

            var s = Oscillate(wave, phase, duty);
            phase += f / srf;
            while (phase >= 1.0f) phase -= 1.0f;

            var env = 1.0f;
            if (i < fade) env = (float)i / fadeF;
            else if (i >= total - fade) env = (float)(total - 1 - i) / fadeF;

            var o = Quantize(s * volume * env, crushSteps);
            for (var c = 0; c < channels; c++) p[i * channels + c] = o;
        }

        return buf;
    }

    /// <summary><see cref="RenderVoice(float,int,int,float,float,float,float,bool)"/> taking a <see cref="Wave"/>.</summary>
    public static ChipBuffer RenderVoice(float freq, Wave wave, int total, float volume, float duty, float freqEnd, float crush, bool sweep)
        => RenderVoice(freq, (int)wave, total, volume, duty, freqEnd, crush, sweep);

    /// <summary>
    /// Render a chord (multiple voices averaged so output stays in -1..+1) as
    /// one interleaved buffer. See <see cref="RenderVoice(float,int,int,float,float,float,float,bool)"/>
    /// for fade/envelope behavior.
    /// </summary>
    public static unsafe ChipBuffer RenderChord(int[] notes, int wave, int total, float volume, float duty)
    {
        var sr = OwnAudioBridge.SampleRate();
        var channels = OwnAudioBridge.Channels();
        if (sr <= 0 || channels <= 0 || total <= 0 || notes == null || notes.Length <= 0) return new ChipBuffer(0);

        var srf = (float)sr;
        var count = notes.Length;
        var countF = (float)count;
        var buf = new ChipBuffer(total * channels);
        var p = (float*)new IntPtr(buf.Address).ToPointer();

        var freqs = new float[count];
        for (var q = 0; q < count; q++) freqs[q] = MidiFreq(notes[q]);

        var fade = Math.Max(16, sr / 200);
        var fadeF = (float)fade;

        var phases = new float[count];
        for (var i = 0; i < total; i++)
        {
            var mix = 0.0f;
            for (var v = 0; v < count; v++)
            {
                mix += Oscillate(wave, phases[v], duty);
                phases[v] += freqs[v] / srf;
                while (phases[v] >= 1.0f) phases[v] -= 1.0f;
            }
            mix /= countF;

            var env = 1.0f;
            if (i < fade) env = (float)i / fadeF;
            else if (i >= total - fade) env = (float)(total - 1 - i) / fadeF;

            var o = mix * volume * env;
            for (var c = 0; c < channels; c++) p[i * channels + c] = o;
        }

        return buf;
    }

    /// <summary><see cref="RenderChord(int[],int,int,float,float)"/> taking a <see cref="Wave"/>.</summary>
    public static ChipBuffer RenderChord(int[] notes, Wave wave, int total, float volume, float duty)
        => RenderChord(notes, (int)wave, total, volume, duty);

    // ── Buffer access helpers (Contract shadow targets) ─────────────────

    private static ChipBuffer Unwrap(object handle)
        => handle is ChipBuffer b ? b : throw new InvalidOperationException("Chip: handle is not a chip audio buffer");

    /// <summary>Raw address of a rendered buffer's sample data (0 when empty/freed).</summary>
    public static long BufferAddress(object handle) => Unwrap(handle).Address;

    /// <summary>Total float samples in a rendered buffer.</summary>
    public static int BufferLength(object handle) => Unwrap(handle).Length;

    /// <summary>Frees a rendered buffer's unmanaged memory. Idempotent.</summary>
    public static void FreeBuffer(object handle) => Unwrap(handle).Free();

    // ── Streaming ───────────────────────────────────────────────────────

    /// <summary>
    /// Spawn a background thread that streams a pre-rendered buffer to the
    /// output ring, then frees it. The calling thread returns immediately and
    /// must not touch <paramref name="bufHandle"/> afterwards (nor free it —
    /// the streamer owns it). Uses pointer arithmetic so no temp copy is made.
    /// </summary>
    public static void SpawnStream(object bufHandle, int totalSamples)
    {
        var buf = Unwrap(bufHandle);
        var channels = OwnAudioBridge.Channels();
        var perCall = OwnAudioBridge.BufferSize();
        if (perCall <= 0) perCall = 512;
        var chunk = perCall * channels;
        var baseAddr = buf.Address;

        new Thread(() =>
        {
            try
            {
                var sent = 0;
                while (sent < totalSamples)
                {
                    var take = Math.Min(chunk, totalSamples - sent);
                    var guard = 0;
                    while (OwnAudioBridge.FreeOutputSamples() < take && guard < 400)
                    {
                        Thread.Sleep(1);
                        guard++;
                    }
                    OwnAudioBridge.SendSamples(baseAddr + (long)sent * 4, take);
                    sent += take;
                }
            }
            finally { buf.Free(); }
        }) { IsBackground = true }.Start();
    }

    // ── High-level API (all fire-and-forget) ────────────────────────────

    /// <summary>Play silence (zero-amplitude spacer) for a duration in seconds.</summary>
    public static void PlaySilence(float seconds)
    {
        var total = SecondsToFrames(seconds);
        if (total <= 0) return;
        var buf = RenderVoice(0.0f, 0, total, 0.0f, 0.5f, 0.0f, 0.0f, false);
        SpawnStream(buf, total * OwnAudioBridge.Channels());
    }

    /// <summary>Play a tone at a specific frequency (Hz). Pre-renders and streams in background.</summary>
    public static void PlayTone(float freq, int wave, float seconds, float volume, float duty)
    {
        var total = SecondsToFrames(seconds);
        if (total <= 0) return;
        var buf = RenderVoice(freq, wave, total, ClampVol(volume), duty, freq, 0.0f, false);
        SpawnStream(buf, total * OwnAudioBridge.Channels());
    }

    /// <summary><see cref="PlayTone(float,int,float,float,float)"/> taking a <see cref="Wave"/>.</summary>
    public static void PlayTone(float freq, Wave wave, float seconds, float volume, float duty)
        => PlayTone(freq, (int)wave, seconds, volume, duty);

    /// <summary>Play a MIDI note with 50% duty cycle.</summary>
    public static void PlayNote(int midi, int wave, float seconds, float volume)
        => PlayNoteDuty(midi, wave, seconds, volume, 0.5f);

    /// <summary>Play a MIDI note with 50% duty cycle.</summary>
    public static void PlayNote(int midi, Wave wave, float seconds, float volume)
        => PlayNote(midi, (int)wave, seconds, volume);

    /// <summary>Play a MIDI note with a custom duty cycle (ideal for <see cref="Wave.Pulse"/>).</summary>
    public static void PlayNoteDuty(int midi, int wave, float seconds, float volume, float duty)
    {
        var total = SecondsToFrames(seconds);
        if (total <= 0) return;
        var f = MidiFreq(midi);
        var buf = RenderVoice(f, wave, total, ClampVol(volume), duty, f, 0.0f, false);
        SpawnStream(buf, total * OwnAudioBridge.Channels());
    }

    /// <summary><see cref="PlayNoteDuty(int,int,float,float,float)"/> taking a <see cref="Wave"/>.</summary>
    public static void PlayNoteDuty(int midi, Wave wave, float seconds, float volume, float duty)
        => PlayNoteDuty(midi, (int)wave, seconds, volume, duty);

    /// <summary>Play a MIDI note with a bit-crush effect (crush = amplitude levels, 4..256).</summary>
    public static void PlayNoteCrush(int midi, int wave, float seconds, float volume, float duty, int crush)
    {
        var total = SecondsToFrames(seconds);
        if (total <= 0) return;
        var f = MidiFreq(midi);
        var buf = RenderVoice(f, wave, total, ClampVol(volume), duty, f, crush, false);
        SpawnStream(buf, total * OwnAudioBridge.Channels());
    }

    /// <summary><see cref="PlayNoteCrush(int,int,float,float,float,int)"/> taking a <see cref="Wave"/>.</summary>
    public static void PlayNoteCrush(int midi, Wave wave, float seconds, float volume, float duty, int crush)
        => PlayNoteCrush(midi, (int)wave, seconds, volume, duty, crush);

    /// <summary>Play a chord (multiple MIDI notes simultaneously, averaged voices).</summary>
    public static void PlayChord(int[] notes, int wave, float seconds, float volume)
    {
        var total = SecondsToFrames(seconds);
        if (total <= 0 || notes == null || notes.Length <= 0) return;
        var buf = RenderChord(notes, wave, total, ClampVol(volume), 0.5f);
        SpawnStream(buf, total * OwnAudioBridge.Channels());
    }

    /// <summary><see cref="PlayChord(int[],int,float,float)"/> taking a <see cref="Wave"/>.</summary>
    public static void PlayChord(int[] notes, Wave wave, float seconds, float volume)
        => PlayChord(notes, (int)wave, seconds, volume);

    /// <summary>
    /// Play a melody: each MIDI note for <paramref name="noteLen"/> seconds, then
    /// <paramref name="gap"/> seconds of silence, pre-rendered into one buffer.
    /// </summary>
    public static unsafe void PlayMelody(int[] notes, float noteLen, int wave, float volume, float gap)
    {
        var sr = OwnAudioBridge.SampleRate();
        var channels = OwnAudioBridge.Channels();
        if (notes == null || notes.Length <= 0 || sr <= 0 || channels <= 0) return;

        var srf = (float)sr;
        var noteFrames = SecondsToFrames(noteLen);
        var gapFrames = SecondsToFrames(gap);
        var oneNote = noteFrames * channels;
        var oneGap = gapFrames * channels;
        var noteCount = notes.Length;
        var totalSamples = (noteFrames + gapFrames) * noteCount * channels;
        if (totalSamples <= 0) return;

        var buf = new ChipBuffer(totalSamples);
        var p = (float*)new IntPtr(buf.Address).ToPointer();

        var fade = Math.Max(16, sr / 200);
        var fadeF = (float)fade;

        var pos = 0;
        for (var ni = 0; ni < noteCount; ni++)
        {
            var f = MidiFreq(notes[ni]);
            var phase = 0.0f;
            for (var j = 0; j < noteFrames; j++)
            {
                var s = Oscillate(wave, phase, 0.5f);
                phase += f / srf;
                while (phase >= 1.0f) phase -= 1.0f;

                var env = 1.0f;
                if (j < fade) env = (float)j / fadeF;
                else if (j >= noteFrames - fade) env = (float)(noteFrames - 1 - j) / fadeF;

                var o = s * ClampVol(volume) * env;
                for (var c = 0; c < channels; c++) p[pos + j * channels + c] = o;
            }
            pos += oneNote;

            for (var g = 0; g < gapFrames; g++)
                for (var c = 0; c < channels; c++)
                    p[pos + g * channels + c] = 0.0f;
            pos += oneGap;
        }

        SpawnStream(buf, totalSamples);
    }

    /// <summary><see cref="PlayMelody(int[],float,int,float,float)"/> taking a <see cref="Wave"/>.</summary>
    public static void PlayMelody(int[] notes, float noteLen, Wave wave, float volume, float gap)
        => PlayMelody(notes, noteLen, (int)wave, volume, gap);

    /// <summary>
    /// Play a frequency slide (glissando) between two MIDI notes, sweeping
    /// linearly over the duration. Great for laser zaps, coins, risers.
    /// </summary>
    public static void PlaySlide(int fromMidi, int toMidi, int wave, float seconds, float volume)
    {
        var total = SecondsToFrames(seconds);
        if (total <= 0) return;
        var f0 = MidiFreq(fromMidi);
        var f1 = MidiFreq(toMidi);
        var buf = RenderVoice(f0, wave, total, ClampVol(volume), 0.5f, f1, 0.0f, true);
        SpawnStream(buf, total * OwnAudioBridge.Channels());
    }

    /// <summary><see cref="PlaySlide(int,int,int,float,float)"/> taking a <see cref="Wave"/>.</summary>
    public static void PlaySlide(int fromMidi, int toMidi, Wave wave, float seconds, float volume)
        => PlaySlide(fromMidi, toMidi, (int)wave, seconds, volume);

    /// <summary>
    /// Play a Standard MIDI File: loads <paramref name="path"/> with OwnAudio.Midi
    /// (the C# reader), walks every track's events collecting note-ons, computes
    /// each note's absolute start honoring set-tempo meta events and its duration
    /// from the matching note-off (or <paramref name="fallback"/> seconds), renders
    /// every voice into one polyphonic buffer (mixed and clipped at ±0.99) and
    /// streams it in the background. Returns false when the file has no notes or
    /// cannot be loaded. Keep demos short (duration resolution is O(events^2)).
    /// </summary>
    public static bool PlayMidiFile(string path, int wave, float volume, float fallback)
    {
        MidiFile file;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            file = MidiFileReader.Read(path);
            if (file == null) return false;
        }
        catch
        {
            return false;
        }

        try { return PlayMidiFileCore(file, wave, ClampVol(volume), Math.Max(0.0f, fallback)); }
        finally { /* MidiFile has no IDisposable; let GC collect it */ }
    }

    /// <summary><see cref="PlayMidiFile(string,int,float,float)"/> taking a <see cref="Wave"/>.</summary>
    public static bool PlayMidiFile(string path, Wave wave, float volume, float fallback)
        => PlayMidiFile(path, (int)wave, volume, fallback);

    private static bool PlayMidiFileCore(MidiFile file, int wave, float volume, float fallback)
    {
        var sr = OwnAudioBridge.SampleRate();
        var channels = OwnAudioBridge.Channels();
        if (sr <= 0 || channels <= 0) return false;
        var srf = (float)sr;
        var tpb = file.TicksPerBeat <= 0 ? 480 : file.TicksPerBeat;
        var tpbF = (float)tpb;

        var noteCount = 0;
        foreach (var track in file.Tracks)
            foreach (var e in track.Events)
                if (e.Type == MidiEventType.Midi && IsNoteOn(e)) noteCount++;
        if (noteCount <= 0) return false;

        var starts = new List<float>(noteCount);
        var midis = new List<int>(noteCount);
        var durs = new List<float>(noteCount);

        foreach (var track in file.Tracks)
        {
            var evs = track.Events;
            var tick = 0.0f;
            var tempoUs = 500000; // 120 BPM default
            for (var i = 0; i < evs.Count; i++)
            {
                var e = evs[i];
                tick += e.DeltaTime;
                if (e.IsTempoChange)
                {
                    var us = e.GetTempoMicroseconds();
                    if (us > 0) tempoUs = us;
                }
                var sec = tick * (tempoUs / 1000000.0f / tpbF);
                if (e.Type != MidiEventType.Midi || !IsNoteOn(e)) continue;
                if (starts.Count >= noteCount) continue;

                var offTicks = FirstOffDelta(evs, i, e.Data1);
                var dur = fallback;
                if (offTicks > 0) dur = offTicks * (tempoUs / 1000000.0f / tpbF);
                if (dur < 0.05f) dur = 0.05f;
                starts.Add(sec);
                midis.Add(e.Data1);
                durs.Add(dur);
            }
        }

        var lastEnd = 0.0f;
        for (var q = 0; q < starts.Count; q++)
            lastEnd = Math.Max(lastEnd, starts[q] + durs[q]);
        var frames = (int)(lastEnd * srf) + 1;
        if (frames <= 0) return false;
        if (frames > s_maxMidiFrames) return false;

        var master = new ChipBuffer(frames * channels);
        FillZero(master);

        for (var q = 0; q < starts.Count; q++)
        {
            var framesNote = (int)(durs[q] * srf);
            if (framesNote < 8) framesNote = 8;
            var fV = MidiFreq(midis[q]);
            var voice = RenderVoice(fV, wave, framesNote, volume, 0.5f, fV, 0.0f, false);
            var offset = (int)(starts[q] * srf);
            AddMixed(voice, master, offset, frames, channels);
            voice.Free();
        }

        SpawnStream(master, frames * channels);
        return true;
    }

    // ~20 minutes at 44.1 kHz stereo budgets the pre-render; keep demos short.
    private const int s_maxMidiFrames = 20 * 60 * 44100;

    private static bool IsNoteOn(MidiEvent e) => e.Status >= 144 && e.Status < 160 && e.Data2 > 0;

    /// <summary>Delta ticks from a note-on event to its matching note-off in the
    /// same track (128..143, or a zero-velocity note-on for the same note).</summary>
    private static int FirstOffDelta(IReadOnlyList<MidiEvent> evs, int onIndex, byte note)
    {
        var delta = 0;
        for (var i = onIndex + 1; i < evs.Count; i++)
        {
            var e = evs[i];
            delta += e.DeltaTime;
            if (e.Type != MidiEventType.Midi) continue;
            var off = (e.Status >= 128 && e.Status < 144)
                      || (e.Status >= 144 && e.Status < 160 && e.Data2 == 0);
            if (off && e.Data1 == note) return delta;
        }
        return 0;
    }

    private static int SecondsToFrames(float seconds)
    {
        var sr = OwnAudioBridge.SampleRate();
        return (int)(seconds * sr);
    }

    private static unsafe void FillZero(ChipBuffer buf)
    {
        if (buf.Length <= 0) return;
        var p = (float*)new IntPtr(buf.Address).ToPointer();
        for (var i = 0; i < buf.Length; i++) p[i] = 0.0f;
    }

    private static unsafe void AddMixed(ChipBuffer voice, ChipBuffer master, int offset, int frames, int channels)
    {
        var vp = (float*)new IntPtr(voice.Address).ToPointer();
        var mp = (float*)new IntPtr(master.Address).ToPointer();
        var masterSamples = frames * channels;
        var baseIdx = offset * channels;
        for (var k = 0; k < voice.Length; k++)
        {
            var mi = baseIdx + k;
            if (mi >= masterSamples) break;
            var sum = mp[mi] + vp[k];
            if (sum > 0.99f) sum = 0.99f;
            if (sum < -0.99f) sum = -0.99f;
            mp[mi] = sum;
        }
    }
}