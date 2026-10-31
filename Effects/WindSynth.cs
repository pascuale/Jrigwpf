using System;

namespace Jrig.Effects;

/// <summary>
/// Guitar-to-wind synth. A low-latency YIN pitch tracker follows the note you play and drives a wavetable
/// voice voiced as a sax, flute, trumpet or clarinet. The guitar's loudness becomes the "breath": the voice
/// swells and fades with your picking but sustains instead of decaying like a string. Each instrument has its
/// own harmonic spectrum, body filter, breath noise, attack and vibrato.
/// Monophonic: play single notes. Chords make the tracker drop out (the wind voice goes silent until it locks again).
/// </summary>
public sealed class WindSynth : EffectBase, IPedalReadout
{
    // ---- pitch tracker (runs on a ~11-12 kHz decimated copy, analysed every Hop samples) ----
    const int Window = 640;            // decimated samples kept
    const int Hop = 160;               // analysis every ~13 ms
    const int Half = 400;              // integration length, anchored on the NEWEST samples (low latency)
    const int MaxLag = 220;            // lowest detectable ~ 50 Hz
    const int MinLag = 6;              // highest detectable ~ 1.9 kHz
    const float YinThreshold = 0.15f;
    const float SilenceRms = 0.003f;
    const int MaxFails = 12;           // ~160 ms of failed analyses before the pitch is considered lost

    // ---- gate ----
    const float GateOn = 0.004f;       // about -48 dBFS
    const float GateOff = 0.0015f;     // about -56 dBFS

    // ---- wavetables: [instrument][mip level][TableSize + 1] ----
    const int TableSize = 2048;
    const int Levels = 6;              // harmonic caps 32, 16, 8, 4, 2, 1
    const int TopHarmonics = 32;

    static readonly string[] InstrumentNames = { "Sax", "Flute", "Trumpet", "Clarinet" };
    static readonly string[] OctaveNames = { "-1 oct", "Normal", "+1 oct" };
    static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private readonly record struct Voice(
        float BaseCut, float DynCut,                      // body low-pass: base + dynamic (opens with breath), Hz
        float FormF, float FormQ, float FormMix,          // resonant "bell/body" band added on top
        float NoiseF, float NoiseQ, float NoiseGain,      // breath noise band and level
        float AttackMs, float VibCents, float VibRate);   // onset softness, natural vibrato depth and speed

    static readonly Voice[] Voices =
    {
        new(2200f, 2600f,  900f, 2.0f, 0.45f, 2500f, 0.8f, 0.30f, 10f, 22f, 5.0f),   // Sax
        new(3600f, 1200f,    0f, 1.0f, 0.00f, 3800f, 0.7f, 0.45f, 22f, 18f, 5.3f),   // Flute
        new(1700f, 5200f, 1200f, 2.5f, 0.60f, 3000f, 0.8f, 0.10f,  6f, 14f, 5.6f),   // Trumpet
        new(2400f, 1400f, 1500f, 2.0f, 0.20f, 3200f, 0.8f, 0.14f, 14f,  8f, 5.0f),   // Clarinet
    };

    static readonly float[][][] Tables = BuildTables();

    /// <summary>Harmonic amplitude of partial n (1 = fundamental) for each instrument.</summary>
    static float Harmonic(int inst, int n) => inst switch
    {
        0 => MathF.Pow(n, -0.85f) * (n >= 2 && n <= 4 ? 1.25f : 1f),                  // sax: full, reedy, strong 2nd-4th
        1 => MathF.Pow(0.28f, n - 1),                                                   // flute: nearly a sine
        2 => MathF.Pow(n, -0.4f) * MathF.Exp(-n / 14f),                                 // trumpet: bright, many strong partials
        _ => (n & 1) == 1 ? MathF.Pow(n, -0.9f) : 0.05f * MathF.Pow(n, -0.9f),         // clarinet: odd harmonics dominate
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
                    if (a <= 0f) continue;
                    for (int i = 0; i < TableSize; i++)
                        t[i] += a * MathF.Sin(Dsp.TwoPi * h * i / TableSize);
                }
                // equal loudness for every instrument and mip level
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
    readonly EffectParameter instr, breathP, vibP, toneP, octP, windP, dryP;

    // ---- tracker state (audio thread only) ----
    readonly Biquad antiAlias = new();
    readonly float[] frame = new float[Window];
    readonly float[] diff = new float[MaxLag + 1];
    readonly float[] cmnd = new float[MaxLag + 1];
    int decim = 4, decimCount, fill;
    float sr = 48000f;
    float freqTarget;                  // tracked guitar pitch, Hz
    bool pitchOk;
    int fails;
    float pendingFreq;                 // candidate for a large pitch jump, accepted once confirmed

    // ---- voice state (audio thread only) ----
    readonly Biquad bodyLp = new(), formant = new(), noiseBp = new();
    EnvFollower env;
    Lfo vib;
    float phase, freqCur, ampCur, vibFade;
    float atkC, relC, glideC;
    bool gateOpen, wasOn;
    uint rng = 0x9E3779B9u;
    int lastInst = -1;

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
        windP = Add("wind", "Wind", 0f, 100f, 80f, " %", ParameterCurve.Linear, 0);
        dryP = Add("dry", "Dry", 0f, 100f, 25f, " %", ParameterCurve.Linear, 0);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        decim = Math.Max(1, (int)MathF.Round(sr / 12000f));
        antiAlias.Lowpass(MathF.Min(2500f, sr / decim * 0.4f), 0.707f, sr);
        antiAlias.Reset();
        Array.Clear(frame, 0, frame.Length);
        decimCount = 0; fill = 0; fails = 0;
        freqTarget = 0f; pendingFreq = 0f; pitchOk = false;

        env.Set(2f, 40f, sr); env.Reset();
        bodyLp.Lowpass(3000f, 0.707f, sr); bodyLp.Reset();
        formant.Reset(); noiseBp.Reset();
        vib.Reset();
        phase = 0f; freqCur = 0f; ampCur = 0f; vibFade = 0f;
        relC = Dsp.TimeToCoef(45f, sr);
        glideC = Dsp.TimeToCoef(20f, sr);
        gateOpen = false; wasOn = false;
        lastInst = -1;                       // forces SetVoice on the first block
        noteOut = -1; voicedOut = false; freqOut = 0f;
    }

    void SetVoice(int inst)
    {
        var v = Voices[inst];
        atkC = Dsp.TimeToCoef(v.AttackMs, sr);
        if (v.FormMix > 0f) formant.Bandpass(v.FormF, v.FormQ, sr);
        noiseBp.Bandpass(v.NoiseF, v.NoiseQ, sr);
        formant.Reset(); noiseBp.Reset();
        lastInst = inst;
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        int inst = Math.Clamp((int)(instr.Value + 0.5f), 0, InstrumentNames.Length - 1);
        if (inst != lastInst) SetVoice(inst);
        Voice v = Voices[inst];
        float[][] mips = Tables[inst];

        int oct = Math.Clamp((int)(octP.Value + 0.5f), 0, 2);
        float octMul = oct == 0 ? 0.5f : (oct == 1 ? 1f : 2f);
        float breath = breathP.Value * 0.02f;                        // 50 % = 1.0x the instrument's natural breath noise
        float vibDepth = vibP.Value * 0.01f * 2f * v.VibCents * 0.000578f;   // fraction of frequency at LFO peak (cents * ln2/1200)
        float toneMul = Dsp.ExpMap(toneP.Value, 0.5f, 2f);           // 0.5 = neutral
        float windG = windP.Value * 0.010f;
        float dryG = dryP.Value * 0.01f;
        float vibStep = 1f / (0.35f * sr);

        float[] tbl = mips[0];
        int ctl = 0;

        for (int i = 0; i < n; i++)
        {
            float x = buf[offset + i];

            // ---- pitch tracker feed ----
            float lp = antiAlias.Process(x);
            if (++decimCount >= decim)
            {
                decimCount = 0;
                frame[fill++] = lp;
                if (fill >= Window)
                {
                    Analyze(sr / decim);
                    Array.Copy(frame, Hop, frame, 0, Window - Hop);
                    fill = Window - Hop;
                }
            }

            // ---- breath = input envelope, gated ----
            float e = env.Next(x);
            if (gateOpen)
            {
                if (e < GateOff) gateOpen = false;
            }
            else if (e > GateOn)
            {
                gateOpen = true;
                pitchOk = false;            // force a fresh, unconfirmed pitch lock for the new note
                fails = 0;
                vibFade = 0f;
            }

            bool on = gateOpen && pitchOk && freqTarget > 0f;
            float target = on ? MathF.Sqrt(MathF.Min(1f, e * 8.3f)) : 0f;   // sqrt compresses the string's decay into a sustained breath
            ampCur = target + (ampCur - target) * (target > ampCur ? atkC : relC);

            // ---- oscillator frequency: snap on note changes, glide on small drifts ----
            if (on)
            {
                float tf = Dsp.Clamp(freqTarget * octMul, 20f, 4000f);
                if (!wasOn || freqCur <= 0f || MathF.Abs(tf - freqCur) > 0.03f * freqCur)
                {
                    freqCur = tf;
                    if (wasOn) vibFade = 0f;      // new note while playing legato: restart the vibrato fade-in
                }
                else freqCur = tf + (freqCur - tf) * glideC;
                vibFade = MathF.Min(1f, vibFade + vibStep);
            }
            wasOn = on;

            // ---- control-rate updates every 32 samples: mip level and dynamic body filter ----
            if ((ctl++ & 31) == 0)
            {
                float f = freqCur > 0f ? freqCur : 100f;
                float allowed = 0.45f * sr / (f * 1.05f);
                int lv = 0;
                while (lv < Levels - 1 && (TopHarmonics >> lv) > allowed) lv++;
                tbl = mips[lv];
                bodyLp.Lowpass((v.BaseCut + v.DynCut * ampCur) * toneMul, 0.707f, sr);
            }

            float o = 0f;
            if (ampCur > 1e-5f)
            {
                float lfo = Lfo.Sine(vib.Advance(v.VibRate, sr));
                float fi = freqCur * (1f + vibDepth * vibFade * lfo);
                phase += fi / sr;
                if (phase >= 1f) phase -= 1f;
                if (phase < 0f) phase += 1f;

                float p = phase * TableSize;
                int ix = Math.Min((int)p, TableSize - 1);
                float fr = p - ix;
                o = tbl[ix] + (tbl[ix + 1] - tbl[ix]) * fr;
            }

            float body = bodyLp.Process(o);
            if (v.FormMix > 0f) body += formant.Process(o) * v.FormMix * 2f;

            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            float noise = noiseBp.Process(((int)rng) * (1f / 2147483648f)) * v.NoiseGain * breath;

            float wind = (body + noise) * ampCur * windG;
            buf[offset + i] = Dsp.SoftLimit(x * dryG + wind);
        }

        voicedOut = gateOpen && pitchOk;
    }

    /// <summary>YIN on the newest Half samples of the frame (so the estimate lags the string by only ~one analysis window).</summary>
    void Analyze(float fs)
    {
        int b = Window - 1;

        double en = 0;
        for (int j = 0; j < Half; j++) { float s = frame[b - j]; en += s * s; }
        if ((float)Math.Sqrt(en / Half) < SilenceRms) { Fail(); return; }

        diff[0] = 0f;
        for (int tau = 1; tau <= MaxLag; tau++)
        {
            float sum = 0f;
            for (int j = 0; j < Half; j++)
            {
                float d = frame[b - j] - frame[b - j - tau];
                sum += d * d;
            }
            diff[tau] = sum;
        }
        cmnd[0] = 1f;
        float running = 0f;
        for (int tau = 1; tau <= MaxLag; tau++)
        {
            running += diff[tau];
            cmnd[tau] = running > 0f ? diff[tau] * tau / running : 1f;
        }

        int best = -1;
        for (int tau = MinLag; tau < MaxLag; tau++)
        {
            if (cmnd[tau] < YinThreshold)
            {
                while (tau + 1 < MaxLag && cmnd[tau + 1] < cmnd[tau]) tau++;
                best = tau; break;
            }
        }
        if (best < 0) { Fail(); return; }

        float lag = best;
        if (best > 1 && best < MaxLag)
        {
            float s0 = cmnd[best - 1], s1 = cmnd[best], s2 = cmnd[best + 1];
            float den = s0 - 2f * s1 + s2;
            if (MathF.Abs(den) > 1e-9f) lag = best + 0.5f * (s0 - s2) / den;
        }
        float f = fs / lag;
        if (f < 40f || f > 1500f) { Fail(); return; }

        fails = 0;
        if (!pitchOk)
        {
            // first lock of a note: accept at once so the voice starts quickly
            freqTarget = f; pitchOk = true; pendingFreq = 0f;
        }
        else if (MathF.Abs(MathF.Log2(f / freqTarget)) > 0.04f)
        {
            // jump: believe it only when two analyses in a row agree (rejects one-frame octave errors)
            if (pendingFreq > 0f && MathF.Abs(MathF.Log2(f / pendingFreq)) < 0.04f)
            {
                freqTarget = f; pendingFreq = 0f;
            }
            else pendingFreq = f;
        }
        else
        {
            pendingFreq = 0f;
            freqTarget += 0.5f * (f - freqTarget);
        }

        if (pitchOk)
        {
            freqOut = freqTarget;
            noteOut = Math.Max(0, (int)MathF.Round(69f + 12f * MathF.Log2(freqTarget / 440f)));
        }
    }

    void Fail()
    {
        if (++fails > MaxFails) { pitchOk = false; pendingFreq = 0f; }
    }
}
