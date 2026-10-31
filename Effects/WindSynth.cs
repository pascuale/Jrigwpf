using System;

namespace Jrig.Effects;

/// <summary>
/// Guitar-to-wind synth. A pitch tracker follows the single note you play and a source-and-body model plays it as a
/// sax, flute, trumpet or clarinet.
///
/// What makes it read as a wind instrument rather than an organ:
///  - The guitar string only triggers the note. Once a note starts, the "breath" is held at the pick's loudness
///    (with a very slow drift) instead of decaying like a string. The note ends when the string has faded to a set
///    fraction of its peak (Hold knob) or you mute it. Picking again re-articulates it.
///  - A bright, harmonically rich source goes through FIXED body resonances (formants), so the timbre changes with the
///    note like a real horn instead of keeping one spectrum. The sound brightens as you play harder.
///  - Each instrument has its own articulation: tongue / chiff noise burst, a small pitch scoop into the note,
///    breath noise (tuned to the note on the flute), delayed and slightly irregular vibrato, and tiny random pitch and
///    level flutter so it never sounds like a machine.
/// Monophonic: play single notes. Chords make the tracker drop out until it locks again.
/// </summary>
public sealed class WindSynth : EffectBase, IPedalReadout
{
    // ---- note lifecycle ----
    const float GateOn = 0.004f;       // about -48 dBFS
    const float GateOff = 0.0015f;     // about -56 dBFS

    // ---- wavetables: [instrument][mip level][TableSize + 1] ----
    const int TableSize = 2048;
    const int Levels = 6;              // harmonic caps 32, 16, 8, 4, 2, 1
    const int TopHarmonics = 32;
    const int MaxFormants = 4;

    static readonly string[] InstrumentNames = { "Sax", "Flute", "Trumpet", "Clarinet" };
    static readonly string[] OctaveNames = { "-1 oct", "Normal", "+1 oct" };
    static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>Everything that makes one instrument sound like itself.</summary>
    private sealed class VoiceDef
    {
        // body: source -> Direct + parallel fixed resonances -> low-pass that opens with breath
        public float[] FormF = Array.Empty<float>();   // resonance centres, Hz
        public float[] FormQ = Array.Empty<float>();
        public float[] FormG = Array.Empty<float>();   // 0 = unused
        public float Direct;                           // un-resonated source mixed in
        public float BaseCut, DynCut;                  // low-pass: base + extra * breath^2, Hz
        public float Drive;                            // brass-style harmonic growth with breath (0 = none)
        // noise
        public float Noise, NoiseF, NoiseQ;            // steady breath noise level and band
        public float Pitched;                          // breath noise tuned to the note (flute)
        // articulation
        public float AttackMs, ReleaseMs;              // how the note swells in and dies out
        public float VibCents, VibRate, VibDelayMs;    // natural vibrato (peak cents), speed, delay before it starts
        public float ScoopCents;                       // pitch the note starts at relative to target (negative = flat)
        public float Burst, BurstMs;                   // tongue / chiff noise at the start of a note
        public float Trim;                             // output level match between instruments
    }

    static readonly VoiceDef[] Voices =
    {
        new VoiceDef   // Sax
        {
            FormF = new[] { 500f, 1000f, 1800f, 3000f }, FormQ = new[] { 1.2f, 1.2f, 1.2f, 1.2f }, FormG = new[] { 1.0f, 0.8f, 0.5f, 0.3f },
            Direct = 0.3f, BaseCut = 3500f, DynCut = 2500f, Drive = 0.8f,
            Noise = 0.25f, NoiseF = 2500f, NoiseQ = 0.8f, Pitched = 0f,
            AttackMs = 12f, ReleaseMs = 60f, VibCents = 22f, VibRate = 5.0f, VibDelayMs = 250f,
            ScoopCents = -30f, Burst = 0.35f, BurstMs = 25f, Trim = 0.68f,
        },
        new VoiceDef   // Flute
        {
            Direct = 1f, BaseCut = 4000f, DynCut = 1500f, Drive = 0f,
            Noise = 0.18f, NoiseF = 4500f, NoiseQ = 0.6f, Pitched = 0.5f,
            AttackMs = 28f, ReleaseMs = 90f, VibCents = 17f, VibRate = 5.3f, VibDelayMs = 300f,
            ScoopCents = -12f, Burst = 0.6f, BurstMs = 45f, Trim = 0.61f,
        },
        new VoiceDef   // Trumpet
        {
            FormF = new[] { 1000f, 1800f, 2800f, 4000f }, FormQ = new[] { 1.2f, 1.2f, 1.2f, 1.2f }, FormG = new[] { 1.0f, 0.8f, 0.5f, 0.3f },
            Direct = 0.4f, BaseCut = 1500f, DynCut = 6500f, Drive = 2.0f,
            Noise = 0.05f, NoiseF = 3000f, NoiseQ = 0.8f, Pitched = 0f,
            AttackMs = 7f, ReleaseMs = 45f, VibCents = 13f, VibRate = 5.6f, VibDelayMs = 450f,
            ScoopCents = -45f, Burst = 0.25f, BurstMs = 20f, Trim = 0.87f,
        },
        new VoiceDef   // Clarinet
        {
            FormF = new[] { 600f, 1500f, 2600f, 0f }, FormQ = new[] { 1.2f, 1.5f, 1.5f, 1f }, FormG = new[] { 1.0f, 0.5f, 0.2f, 0f },
            Direct = 0.5f, BaseCut = 3000f, DynCut = 1200f, Drive = 0f,
            Noise = 0.06f, NoiseF = 3200f, NoiseQ = 0.8f, Pitched = 0f,
            AttackMs = 16f, ReleaseMs = 50f, VibCents = 6f, VibRate = 5.0f, VibDelayMs = 600f,
            ScoopCents = 0f, Burst = 0.1f, BurstMs = 20f, Trim = 0.65f,
        },
    };

    static readonly float[][][] Tables = BuildTables();

    /// <summary>Source spectrum: harmonic amplitude of partial n (1 = fundamental). Bodies shape it afterwards.</summary>
    static float Harmonic(int inst, int n) => inst switch
    {
        0 => MathF.Pow(n, -0.9f),                                              // sax: reed, bright, all harmonics
        1 => MathF.Pow(0.25f, n - 1),                                          // flute: nearly a sine
        2 => MathF.Pow(n, -0.7f),                                              // trumpet: buzzing lips, very bright
        _ => (n & 1) == 1 ? MathF.Pow(n, -1.0f) : 0.03f * MathF.Pow(n, -1.0f), // clarinet: odd harmonics only (closed cylinder)
    };

    static float[][][] BuildTables()
    {
        var all = new float[InstrumentNames.Length][][];
        for (int inst = 0; inst < all.Length; inst++)
        {
            all[inst] = new float[Levels][];
            for (int lv = 0; lv < Levels; lv++)
            {
                int cap = TopHarmonics >> lv;
                var t = new float[TableSize + 1];
                for (int h = 1; h <= cap; h++)
                {
                    float a = Harmonic(inst, h);
                    for (int i = 0; i < TableSize; i++)
                        t[i] += a * MathF.Sin(Dsp.TwoPi * h * i / TableSize);
                }
                double e = 0;
                for (int i = 0; i < TableSize; i++) e += t[i] * t[i];
                float rms = (float)Math.Sqrt(e / TableSize);
                float scale = rms > 1e-9f ? 0.30f / rms : 0f;
                for (int i = 0; i < TableSize; i++) t[i] *= scale;
                t[TableSize] = t[0];
                all[inst][lv] = t;
            }
        }
        return all;
    }

    // ---- parameters ----
    readonly EffectParameter instr, breathP, vibP, toneP, octP, holdP, windP, dryP;

    // ---- state (audio thread only) ----
    readonly PitchTracker tracker = new();
    readonly Biquad bodyLp = new(), noiseBp = new(), pitchBp1 = new(), pitchBp2 = new();
    readonly Biquad[] formants = { new Biquad(), new Biquad(), new Biquad(), new Biquad() };
    EnvFollower env;
    Lfo vib;
    float sr = 48000f;
    float phase, freqCur, ampCur, lag, peak, scoop, burst;
    float atkC, relC, glideC, scoopC, burstC;
    float j1, j2, j3;                              // slow random flutter: pitch, level, vibrato speed
    int sinceOnset, vibT, lastInst = -1;
    bool noteOn, armed = true, wasOn;
    uint rng = 0x9E3779B9u, rngB = 0x1234567u;

    // ---- readout (thread-safe snapshots for the UI) ----
    volatile int noteOut = -1;
    volatile float freqOut;
    volatile bool voicedOut;

    public string Headline => voicedOut && noteOut >= 0 ? NoteNames[noteOut % 12] + (noteOut / 12 - 1) : "--";
    public string Detail => voicedOut
        ? $"{InstrumentNames[Math.Clamp((int)(instr.Value + 0.5f), 0, InstrumentNames.Length - 1)]}   {freqOut:F1} Hz"
        : "play a single note";
    public bool HasNeedle => false;
    public float Needle => 0f;
    public bool HasProgress => false;
    public float Progress => 0f;

    public WindSynth() : base("Wind Synth")
    {
        instr = Add("instrument", "Instrument", 0f, InstrumentNames.Length - 1, 0f, "", ParameterCurve.Linear, 0);
        instr.Labels = InstrumentNames;
        breathP = Add("breath", "Breath", 0f, 100f, 50f, " %", ParameterCurve.Linear, 0);
        vibP = Add("vibrato", "Vibrato", 0f, 100f, 40f, " %", ParameterCurve.Linear, 0);
        toneP = Add("tone", "Tone", 0f, 1f, 0.5f, "", ParameterCurve.Linear, 2);
        octP = Add("octave", "Octave", 0f, 2f, 1f, "", ParameterCurve.Linear, 0);
        octP.Labels = OctaveNames;
        holdP = Add("hold", "Hold", 0f, 100f, 75f, " %", ParameterCurve.Linear, 0);
        windP = Add("wind", "Wind", 0f, 100f, 80f, " %", ParameterCurve.Linear, 0);
        dryP = Add("dry", "Dry", 0f, 100f, 25f, " %", ParameterCurve.Linear, 0);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        tracker.Prepare(sr);
        env.Set(2f, 40f, sr); env.Reset();
        bodyLp.Lowpass(3000f, 0.707f, sr); bodyLp.Reset();
        noiseBp.Reset(); pitchBp1.Reset(); pitchBp2.Reset();
        foreach (var f in formants) f.Reset();
        vib.Reset();
        phase = 0f; freqCur = 0f; ampCur = 0f; lag = 0f; peak = 0f; scoop = 0f; burst = 0f;
        j1 = j2 = j3 = 0f;
        sinceOnset = int.MaxValue / 2; vibT = 0;
        relC = Dsp.TimeToCoef(45f, sr);
        glideC = Dsp.TimeToCoef(20f, sr);
        scoopC = Dsp.TimeToCoef(35f, sr);
        noteOn = false; armed = true; wasOn = false;
        lastInst = -1;                       // forces SetVoice on the first block
        noteOut = -1; voicedOut = false; freqOut = 0f;
    }

    void SetVoice(int inst)
    {
        VoiceDef v = Voices[inst];
        atkC = Dsp.TimeToCoef(v.AttackMs, sr);
        relC = Dsp.TimeToCoef(v.ReleaseMs, sr);
        burstC = Dsp.TimeToCoef(v.BurstMs, sr);
        for (int k = 0; k < MaxFormants; k++)
        {
            if (k < v.FormG.Length && v.FormG[k] > 0f) formants[k].Bandpass(v.FormF[k], v.FormQ[k], sr);
            formants[k].Reset();
        }
        noiseBp.Bandpass(v.NoiseF, v.NoiseQ, sr);
        noiseBp.Reset(); pitchBp1.Reset(); pitchBp2.Reset();
        lastInst = inst;
    }

    /// <summary>White noise in -1..+1 (xorshift). Filtered by a one-pole in the caller to make slow flutter.</summary>
    static float White(ref uint s)
    {
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
        return ((int)s) * (1f / 2147483648f);
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        int inst = Math.Clamp((int)(instr.Value + 0.5f), 0, InstrumentNames.Length - 1);
        if (inst != lastInst) SetVoice(inst);
        VoiceDef v = Voices[inst];
        float[][] mips = Tables[inst];

        int oct = Math.Clamp((int)(octP.Value + 0.5f), 0, 2);
        float octMul = oct == 0 ? 0.5f : (oct == 1 ? 1f : 2f);
        float breath = breathP.Value * 0.02f;                              // 50 % = 1.0x the instrument's natural breath noise
        float vibDepth = vibP.Value * 0.01f * 2f * v.VibCents * 0.000578f; // fraction of frequency at LFO peak (cents * ln2/1200)
        float toneMul = Dsp.ExpMap(toneP.Value, 0.5f, 2f);                 // 0.5 = neutral
        float windG = windP.Value * 0.010f * v.Trim;
        float dryG = dryP.Value * 0.01f;
        float relThr = 0.5f - 0.46f * holdP.Value * 0.01f;                 // note ends when the string is below this fraction of its peak
        float scoopAmt = v.ScoopCents * 0.000578f;
        int vibDelay = (int)(v.VibDelayMs * 0.001f * sr);
        float vibRamp = 1f / (0.4f * sr);
        int gap = (int)(0.06f * sr), holdMin = (int)(0.08f * sr), peakWin = (int)(0.025f * sr);
        float lagC = 1f / (0.026f * sr);
        const float droop = 0.99999f;                                      // about -4 dB per second of natural breath drift
        const float wander = 0.0005f;                                      // corner of the flutter, about 4 Hz

        float[] tbl = mips[0];
        int ctl = 0;

        for (int i = 0; i < n; i++)
        {
            float x = buf[offset + i];
            tracker.Process(x);

            // ---- note lifecycle: the string triggers it, the breath holds it ----
            float e = env.Next(x);
            lag += (e - lag) * lagC;
            if (sinceOnset < int.MaxValue / 2) sinceOnset++;
            if (e < GateOff) armed = true;
            if (noteOn && (e < GateOff || (sinceOnset > holdMin && e < peak * relThr))) noteOn = false;
            if (e > GateOn && (armed || (e > 1.6f * lag + 0.002f && sinceOnset > gap)))
            {
                armed = false; noteOn = true; peak = e; sinceOnset = 0;
                tracker.Unlock();           // fresh, immediately accepted pitch lock for the new note
            }
            if (noteOn)
            {
                if (sinceOnset < peakWin) { if (e > peak) peak = e; }
                else peak *= droop;
            }

            bool on = noteOn && tracker.Locked && tracker.Frequency > 0f;
            float target = on ? MathF.Sqrt(MathF.Min(1f, peak * 8.3f)) : 0f;
            ampCur = target + (ampCur - target) * (target > ampCur ? atkC : relC);

            // ---- pitch: snap on note changes, glide on small drifts, scoop into a new note ----
            if (on)
            {
                float tf = Dsp.Clamp(tracker.Frequency * octMul, 20f, 4000f);
                if (!wasOn)
                {
                    freqCur = tf; scoop = 1f; burst = 1f; vibT = 0;     // a fresh note
                }
                else if (MathF.Abs(tf - freqCur) > 0.03f * freqCur)
                {
                    freqCur = tf; scoop = 0.4f; burst = 0.3f;           // legato to a new pitch: smaller scoop and puff
                }
                else freqCur = tf + (freqCur - tf) * glideC;
                vibT++;
            }
            wasOn = on;
            scoop *= scoopC;
            burst *= burstC;

            // ---- slow random flutter so the voice never sounds mechanical ----
            j1 += (White(ref rng) - j1) * wander;
            j2 += (White(ref rngB) - j2) * wander;
            j3 += (White(ref rng) - j3) * wander;

            // ---- control rate (every 32 samples): mip level, breath-dependent body filter, tuned flute noise ----
            if ((ctl++ & 31) == 0)
            {
                float f = freqCur > 0f ? freqCur : 100f;
                float allowed = 0.45f * sr / (f * 1.05f);
                int lv = 0;
                while (lv < Levels - 1 && (TopHarmonics >> lv) > allowed) lv++;
                tbl = mips[lv];
                bodyLp.Lowpass((v.BaseCut + v.DynCut * ampCur * ampCur) * toneMul, 0.707f, sr);
                if (v.Pitched > 0f && freqCur > 0f)
                {
                    pitchBp1.Bandpass(freqCur, 5f, sr);
                    pitchBp2.Bandpass(freqCur * 2f, 6f, sr);
                }
            }

            // ---- oscillator ----
            float o = 0f;
            if (ampCur > 1e-5f)
            {
                float vibEnv = Dsp.Clamp((vibT - vibDelay) * vibRamp, 0f, 1f);
                float lfo = Lfo.Sine(vib.Advance(v.VibRate * (1f + 6f * j3), sr));
                float fi = freqCur * (1f + scoopAmt * scoop + vibDepth * vibEnv * lfo + 0.16f * j1);
                phase += fi / sr;
                if (phase >= 1f) phase -= 1f;
                if (phase < 0f) phase += 1f;

                float p = phase * TableSize;
                int ix = Math.Min((int)p, TableSize - 1);
                float fr = p - ix;
                o = tbl[ix] + (tbl[ix + 1] - tbl[ix]) * fr;

                if (v.Drive > 0f)
                {
                    // brass-style: harder playing grows the upper harmonics, level stays put
                    float g = 1f + v.Drive * ampCur * ampCur * 3f;
                    o = Dsp.Tanh(o * g) * (0.3f / Dsp.Tanh(0.3f * g));
                }
            }

            // ---- body: source through fixed resonances, then the breath-dependent low-pass ----
            float body = v.Direct * o;
            for (int k = 0; k < v.FormG.Length; k++)
            {
                float y = formants[k].Process(o);
                body += y * v.FormG[k];
            }
            body = bodyLp.Process(body);

            // ---- breath noise and tongue / chiff burst ----
            float white = White(ref rngB);
            float nz = noiseBp.Process(white) * (v.Noise * breath * ampCur + v.Burst * burst);
            if (v.Pitched > 0f)
                nz += (pitchBp1.Process(white) + pitchBp2.Process(white)) * v.Pitched * breath * ampCur;

            float flutter = 1f + 3.3f * j2;                                 // about 3 % level wander
            float wind = (body * ampCur * flutter + nz) * windG;
            buf[offset + i] = Dsp.SoftLimit(x * dryG + wind);
        }

        voicedOut = noteOn && tracker.Locked;
        if (voicedOut)
        {
            float f0 = tracker.Frequency;
            freqOut = f0;
            if (f0 > 0f) noteOut = Math.Max(0, (int)MathF.Round(69f + 12f * MathF.Log2(f0 / 440f)));
        }
    }
}
