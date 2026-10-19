using System.IO;
using NAudio.Wave;

namespace Jrig.Audio;

/// <summary>What the simulated guitar plays. Pick one in the SOURCE box.</summary>
public enum SimulatedGuitarMode
{
    Riff,           // single-note E minor riff: good all-round test
    ScaleRun,       // up and down the E minor pentatonic across all strings
    Chords,         // strummed Em - C - G - D: polyphonic, good for chorus / phaser / delay / octave limits
    Dynamics,       // same note picked soft to hard: compressor, overdrive, envelope filter
    OpenStrings,    // each open string rings for 3.5 s: tuner test
    Drone,          // steady sawtooth A2: wah, phaser, chorus, filters
    SingleNote,     // one open low E that rings and fades; press Pick to hit it again
    AudioFile       // loops a recording you choose (a real DI guitar track is ideal)
}

/// <summary>
/// A stand-in for the Guitar Link so jrig can be tested with no interface and no guitar.
/// Plucked strings use Karplus-Strong synthesis (a delay loop with a damping filter and an
/// all-pass for exact tuning), played from a looping note sequence in standard tuning, then
/// run through a light pickup-style filter.
///
/// Render() runs on the audio thread: everything it uses is allocated in the constructor,
/// Prepare() or LoadFile(), all of which run on the UI thread.
/// </summary>
public sealed class SimulatedGuitar
{
    private const int Strings = 6;
    private const int MaxRing = 4096;                       // longest string loop (lowest note ~12 Hz at 48 kHz)
    private const float OutputScale = 0.45f;                // calibrated so loud strums peak around 0.65

    private static readonly int[] OpenMidi = { 40, 45, 50, 55, 59, 64 };   // E2 A2 D3 G3 B3 E4, low to high

    private struct Ev { public long Time; public int String; public int Fret; public float Vel; }

    private sealed class Pattern
    {
        public Ev[] Events = Array.Empty<Ev>();
        public long Length;
    }

    // ------------------------------------------------------------------ one plucked string

    private sealed class Voice
    {
        private readonly float[] _ring = new float[MaxRing];
        private readonly float[] _tmp = new float[MaxRing];
        private int _len = 8, _pos;
        private float _prev, _apX, _apY, _c, _decay = 1f;

        public void Reset()
        {
            Array.Clear(_ring, 0, _ring.Length);
            _pos = 0; _prev = _apX = _apY = 0f;
        }

        public void Pluck(float freq, float vel, float sr, ref uint rng)
        {
            float period = sr / freq;
            int n = Math.Clamp((int)MathF.Floor(period - 1f), 8, MaxRing - 1);
            float frac = period - 0.5f - n;                     // 0.5 .. 1.5 samples of all-pass delay
            _c = (1f - frac) / (1f + frac);

            // 60 dB decay time in seconds (higher harmonics die faster through the damping filter).
            const float t60 = 8f;
            _decay = MathF.Pow(10f, -3f / (freq * t60));

            if (n != _len)
            {
                Array.Clear(_ring, 0, _ring.Length);            // new pitch: start the loop clean
                _len = n; _pos = 0; _prev = _apX = _apY = 0f;
            }

            // A new pick partly damps a string that is still ringing, so repeated notes do not pile up.
            for (int i = 0; i < n; i++) _ring[i] *= 0.35f;

            // Excitation: noise, low-passed more for soft picking, with a pick-position comb.
            float a = 0.93f - 0.55f * vel;                      // harder pick = brighter
            float lp = 0f, mean = 0f;
            for (int i = 0; i < n; i++)
            {
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float noise = (rng >> 8) * (2f / 16777216f) - 1f;
                lp = a * lp + (1f - a) * noise;
                _tmp[i] = lp;
            }
            int pick = Math.Max(1, (int)(n * 0.17f));
            for (int i = n - 1; i >= pick; i--) _tmp[i] -= _tmp[i - pick];
            for (int i = 0; i < n; i++) mean += _tmp[i];
            mean /= n;

            double energy = 0;
            for (int i = 0; i < n; i++) { _tmp[i] -= mean; energy += _tmp[i] * _tmp[i]; }
            float rms = (float)Math.Sqrt(energy / n);
            float gain = rms > 1e-9f ? 0.32f * (0.08f + 0.92f * MathF.Pow(vel, 1.5f)) / rms : 0f;   // ~14 dB between soft and hard

            for (int i = 0; i < n; i++) _ring[i] += _tmp[i] * gain;      // added on top of the damped ring, so re-picking does not click
        }

        public float Tick()
        {
            float x = _ring[_pos];
            float f = 0.5f * (x + _prev) * _decay;              // damping low-pass and per-pass decay
            _prev = x;
            float ap = _c * f + _apX - _c * _apY;               // fractional delay for exact tuning
            _apX = f;
            _apY = MathF.Abs(ap) < 1e-18f ? 0f : ap;
            _ring[_pos] = _apY;
            if (++_pos >= _len) _pos = 0;
            return x;
        }
    }

    // ------------------------------------------------------------------ state

    private readonly Voice[] _voices = new Voice[Strings];
    private readonly Pattern?[] _patterns = new Pattern?[Enum.GetValues<SimulatedGuitarMode>().Length];

    private volatile int _mode = (int)SimulatedGuitarMode.Riff;
    private int _activeMode = -1;
    private int _sr;
    private long _pos;
    private int _evIdx;
    private uint _rng = 2463534242u;
    private int _pickPending;                               // set by Pick() on the UI thread, consumed on the audio thread

    // pickup-style filtering (one-pole high-pass ~70 Hz, low-pass ~5 kHz)
    private float _hpState, _lpState, _hpA, _lpA;

    // drone
    private float _phase, _breath, _droneLp;

    // audio file
    private float[]? _fileRaw;
    private int _fileRate;
    private float[]? _file;
    private float[]? _lastFile;
    private int _filePos;

    public SimulatedGuitar()
    {
        for (int i = 0; i < Strings; i++) _voices[i] = new Voice();
    }

    public SimulatedGuitarMode Mode
    {
        get => (SimulatedGuitarMode)_mode;
        set => _mode = (int)value;
    }

    public bool HasFile => _fileRaw != null;

    /// <summary>
    /// Hits the string again in <see cref="SimulatedGuitarMode.SingleNote"/> mode. Safe to call from the UI thread.
    /// If the note is still ringing, the new pick damps it a little and adds on top, like a real string.
    /// </summary>
    public void Pick() => Interlocked.Exchange(ref _pickPending, 1);
    public string? FilePath { get; private set; }

    /// <summary>UI thread, before audio starts: builds the note sequences for this sample rate.</summary>
    public void Prepare(int sampleRate)
    {
        _sr = sampleRate;
        foreach (var v in _voices) v.Reset();

        _patterns[(int)SimulatedGuitarMode.Riff] = BuildRiff();
        _patterns[(int)SimulatedGuitarMode.ScaleRun] = BuildScaleRun();
        _patterns[(int)SimulatedGuitarMode.Chords] = BuildChords();
        _patterns[(int)SimulatedGuitarMode.Dynamics] = BuildDynamics();
        _patterns[(int)SimulatedGuitarMode.OpenStrings] = BuildOpenStrings();

        _hpA = 1f - MathF.Exp(-2f * MathF.PI * 70f / sampleRate);
        _lpA = 1f - MathF.Exp(-2f * MathF.PI * 5000f / sampleRate);
        _hpState = _lpState = _droneLp = 0f;
        _phase = _breath = 0f;
        _pos = 0; _evIdx = 0; _activeMode = -1;

        RebuildFile();
    }

    // ------------------------------------------------------------------ audio file

    /// <summary>UI thread. Loads a recording (wav, mp3, aiff, flac, ...) to loop. Throws if it cannot be read.</summary>
    public void LoadFile(string path)
    {
        using var reader = new AudioFileReader(path);
        int ch = reader.WaveFormat.Channels;
        int rate = reader.WaveFormat.SampleRate;
        long maxFrames = (long)rate * 180;                      // 3 minutes is plenty for testing

        var mono = new List<float>();
        var buf = new float[4096 * ch];
        int read;
        while (mono.Count < maxFrames && (read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i + ch <= read; i += ch)
            {
                float s = 0f;
                for (int c = 0; c < ch; c++) s += buf[i + c];
                mono.Add(s / ch);
            }
        }

        if (mono.Count < rate / 10)
            throw new InvalidDataException("That audio file is too short to loop.");

        _fileRaw = mono.ToArray();
        _fileRate = rate;
        FilePath = path;
        if (_sr > 0) RebuildFile();
    }

    private void RebuildFile()
    {
        var raw = _fileRaw;
        if (raw == null || _sr <= 0) return;

        // Linear resample to the engine rate, then normalise so the loudest peak is 0.5.
        int outLen = (int)((long)raw.Length * _sr / _fileRate);
        var res = new float[Math.Max(1, outLen)];
        double step = (double)_fileRate / _sr;
        float peak = 1e-6f;
        for (int i = 0; i < res.Length; i++)
        {
            double p = i * step;
            int i0 = (int)p;
            int i1 = Math.Min(i0 + 1, raw.Length - 1);
            float f = (float)(p - i0);
            float v = raw[Math.Min(i0, raw.Length - 1)] * (1f - f) + raw[i1] * f;
            res[i] = v;
            float a = MathF.Abs(v);
            if (a > peak) peak = a;
        }
        float norm = 0.5f / peak;
        for (int i = 0; i < res.Length; i++) res[i] *= norm;

        Volatile.Write(ref _file, res);
    }

    // ------------------------------------------------------------------ note sequences

    private static float Hz(int midi) => 440f * MathF.Pow(2f, (midi - 69) / 12f);

    private long Samples(double beats, double bpm) => (long)(beats * 60.0 / bpm * _sr);

    private Pattern Finish(List<Ev> list, double lengthBeats, double bpm)
    {
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return new Pattern { Events = list.ToArray(), Length = Samples(lengthBeats, bpm) };
    }

    private Pattern BuildRiff()
    {
        const double bpm = 100;
        // (string, fret, beats, velocity): low-string E minor pentatonic riff, two bars
        var notes = new (int s, int f, double beats, double v)[]
        {
            (0,0,.5,.90),(0,0,.5,.60),(0,3,.5,.80),(0,0,.5,.60),(0,5,.5,.85),(0,0,.5,.60),(0,3,.5,.75),(1,2,.5,.80),
            (1,0,.5,.90),(1,0,.5,.60),(1,2,.5,.80),(1,0,.5,.60),(0,5,.5,.80),(0,3,.5,.70),(0,0,1.0,.90),
        };
        var list = new List<Ev>();
        double t = 0;
        foreach (var n in notes)
        {
            list.Add(new Ev { Time = Samples(t, bpm), String = n.s, Fret = n.f, Vel = (float)n.v });
            t += n.beats;
        }
        return Finish(list, t, bpm);
    }

    private Pattern BuildScaleRun()
    {
        const double bpm = 120;
        var up = new (int s, int f)[]
        {
            (0,0),(0,3),(1,0),(1,2),(2,0),(2,2),(3,0),(3,2),(4,0),(4,3),(5,0),(5,3),(5,5),
        };
        var seq = new List<(int s, int f)>(up);
        for (int i = up.Length - 2; i >= 0; i--) seq.Add(up[i]);

        var list = new List<Ev>();
        double t = 0;
        for (int i = 0; i < seq.Count; i++)
        {
            list.Add(new Ev { Time = Samples(t, bpm), String = seq[i].s, Fret = seq[i].f, Vel = 0.70f + 0.15f * (i % 2) });
            t += 0.5;
        }
        return Finish(list, t + 1.5, bpm);                      // a short rest before it repeats
    }

    private Pattern BuildChords()
    {
        const double bpm = 90;
        // frets low E to high e, -1 = muted
        int[][] chords =
        {
            new[] { 0, 2, 2, 0, 0, 0 },     // Em
            new[] { -1, 3, 2, 0, 1, 0 },    // C
            new[] { 3, 2, 0, 0, 0, 3 },     // G
            new[] { -1, -1, 0, 2, 3, 2 },   // D
        };
        // (beat, down?, velocity)
        var rhythm = new (double beat, bool down, float v)[]
        {
            (0,true,.90f),(1,true,.65f),(1.5,false,.55f),(2.5,false,.60f),(3,true,.75f),(3.5,false,.55f),
        };

        var list = new List<Ev>();
        for (int bar = 0; bar < chords.Length; bar++)
        {
            foreach (var (beat, down, vel) in rhythm)
            {
                long t0 = Samples(bar * 4 + beat, bpm);
                long stagger = (long)((down ? 0.012 : 0.009) * _sr);
                int k = 0;
                for (int j = 0; j < Strings; j++)
                {
                    int s = down ? j : Strings - 1 - j;
                    if (chords[bar][s] < 0) continue;
                    if (!down && s < 2) continue;               // up-strokes only catch the top strings
                    list.Add(new Ev { Time = t0 + k * stagger, String = s, Fret = chords[bar][s], Vel = vel * (down ? 1f : 0.9f) });
                    k++;
                }
            }
        }
        return Finish(list, chords.Length * 4, bpm);
    }

    private Pattern BuildDynamics()
    {
        const double bpm = 80;
        float[] vels = { .20f, .40f, .60f, .80f, 1.0f, .80f, .60f, .40f, 1.0f, .25f, 1.0f, .25f };
        var list = new List<Ev>();
        for (int i = 0; i < vels.Length; i++)
            list.Add(new Ev { Time = Samples(i, bpm), String = 1, Fret = 0, Vel = vels[i] });
        return Finish(list, vels.Length, bpm);
    }

    private Pattern BuildOpenStrings()
    {
        const double bpm = 60;                                  // 1 beat = 1 second
        var list = new List<Ev>();
        for (int s = 0; s < Strings; s++)
            list.Add(new Ev { Time = Samples(s * 3.5, bpm), String = s, Fret = 0, Vel = 0.80f });
        return Finish(list, Strings * 3.5, bpm);
    }

    // ------------------------------------------------------------------ audio thread

    /// <summary>Fills <paramref name="dest"/> with <paramref name="frames"/> mono guitar samples.</summary>
    public void Render(float[] dest, int frames)
    {
        int mode = _mode;
        if (mode != _activeMode)
        {
            _activeMode = mode;                                 // mode switched live: restart that sequence
            _pos = 0; _evIdx = 0; _filePos = 0;

            if (mode == (int)SimulatedGuitarMode.SingleNote)
            {
                foreach (var v in _voices) v.Reset();           // silence anything left ringing from another mode
                _pickPending = 1;                               // play the first note straight away
            }
        }

        switch ((SimulatedGuitarMode)mode)
        {
            case SimulatedGuitarMode.AudioFile: RenderFile(dest, frames); return;
            case SimulatedGuitarMode.Drone: RenderDrone(dest, frames); return;
            case SimulatedGuitarMode.SingleNote: RenderSingleNote(dest, frames); return;
            default: RenderPattern(dest, frames, _patterns[mode]); return;
        }
    }

    private void RenderPattern(float[] dest, int frames, Pattern? p)
    {
        if (p == null || p.Length <= 0) { Array.Clear(dest, 0, frames); return; }

        var evs = p.Events;
        for (int i = 0; i < frames; i++)
        {
            while (_evIdx < evs.Length && evs[_evIdx].Time <= _pos)
            {
                ref Ev e = ref evs[_evIdx++];
                _voices[e.String].Pluck(Hz(OpenMidi[e.String] + e.Fret), e.Vel, _sr, ref _rng);
            }

            float s = 0f;
            for (int v = 0; v < Strings; v++) s += _voices[v].Tick();

            dest[i] = Pickup(s) * OutputScale;

            if (++_pos >= p.Length) { _pos = 0; _evIdx = 0; }
        }
    }

    private void RenderSingleNote(float[] dest, int frames)
    {
        Voice string0 = _voices[0];                             // low E string, open (E2, 82.4 Hz)

        if (Interlocked.Exchange(ref _pickPending, 0) == 1)
            string0.Pluck(Hz(OpenMidi[0]), 0.85f, _sr, ref _rng);

        for (int i = 0; i < frames; i++)
            dest[i] = Pickup(string0.Tick()) * OutputScale;
    }

    private void RenderDrone(float[] dest, int frames)
    {
        const float freq = 110f;                                // A2
        float dt = freq / _sr;
        float lpA = 1f - MathF.Exp(-2f * MathF.PI * 1800f / _sr);
        float breathInc = 0.3f / _sr;

        for (int i = 0; i < frames; i++)
        {
            float saw = 2f * _phase - 1f - PolyBlep(_phase, dt);
            _phase += dt; if (_phase >= 1f) _phase -= 1f;
            _droneLp += lpA * (saw - _droneLp);

            _breath += breathInc; if (_breath >= 1f) _breath -= 1f;
            float amp = 0.85f + 0.15f * MathF.Sin(2f * MathF.PI * _breath);

            dest[i] = Pickup(_droneLp) * 0.22f * amp;           // peaks near 0.3, like a picked note
        }
    }

    private static float PolyBlep(float t, float dt)
    {
        if (t < dt) { t /= dt; return t + t - t * t - 1f; }
        if (t > 1f - dt) { t = (t - 1f) / dt; return t * t + t + t + 1f; }
        return 0f;
    }

    private void RenderFile(float[] dest, int frames)
    {
        float[]? f = Volatile.Read(ref _file);
        if (f == null || f.Length == 0) { Array.Clear(dest, 0, frames); return; }
        if (!ReferenceEquals(f, _lastFile)) { _lastFile = f; _filePos = 0; }

        int pos = _filePos;
        for (int i = 0; i < frames; i++)
        {
            dest[i] = f[pos];
            if (++pos >= f.Length) pos = 0;
        }
        _filePos = pos;
    }

    /// <summary>Gentle pickup-like colouring: roll off below ~70 Hz and above ~5 kHz.</summary>
    private float Pickup(float x)
    {
        _hpState += _hpA * (x - _hpState);
        float y = x - _hpState;
        _lpState += _lpA * (y - _lpState);
        return _lpState;
    }
}
