using System;

namespace Jrig.Effects;

/// <summary>
/// Guitar-to-bass. A pitch tracker follows the single note you play and a bass voice plays it one or two octaves
/// lower. Unlike the Wind Synth, the bass follows the string: it hits when you pick and dies away as the string does
/// (the Sustain knob holds the tail up when you want it to ring). Three voicings: Finger (round, warm), Pick (bright,
/// clanky) and Synth (saw with a filter that snaps open on each pick).
/// Monophonic: play single notes. Chords make the tracker drop out until it locks again.
/// </summary>
public sealed class BassSynth : EffectBase, IPedalReadout
{
    const float GateOn = 0.004f;       // about -48 dBFS
    const float GateOff = 0.0015f;     // about -56 dBFS

    const int TableSize = 2048;
    const int Levels = 6;              // harmonic caps 32, 16, 8, 4, 2, 1
    const int TopHarmonics = 32;

    static readonly string[] VoiceNames = { "Finger", "Pick", "Synth" };
    static readonly string[] OctaveNames = { "-1 oct", "-2 oct" };
    static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private readonly record struct Voice(
        float BaseCut, float DynCut, float Q,             // body low-pass: base + dynamic (opens with picking strength), Hz, resonance
        float AttackMs, float ReleaseMs);                 // how fast the note rises and falls once the string stops

    static readonly Voice[] Voices =
    {
        new( 450f,  700f, 0.707f, 4f, 55f),   // Finger: dark, round
        new(1100f, 2200f, 0.707f, 2f, 45f),   // Pick: bright, quick
        new( 350f, 3200f, 2.0f,   2f, 40f),   // Synth: resonant filter that opens on the pick
    };

    static readonly float[][][] Tables = BuildTables();

    /// <summary>Harmonic amplitude of partial n (1 = fundamental) for each voice.</summary>
    static float Harmonic(int voice, int n) => voice switch
    {
        0 => MathF.Pow(n, -1.3f) * (n == 2 ? 1.2f : 1f),   // finger: strong fundamental and 2nd, fast rolloff
        1 => MathF.Pow(n, -0.9f) * (n >= 3 && n <= 6 ? 1.2f : 1f),   // pick: bright mids
        _ => 1f / n,                                        // synth: sawtooth
    };

    static float[][][] BuildTables()
    {
        var all = new float[VoiceNames.Length][][];
        for (int v = 0; v < all.Length; v++)
        {
            all[v] = new float[Levels][];
            for (int lv = 0; lv < Levels; lv++)
            {
                int cap = TopHarmonics >> lv;
                var t = new float[TableSize + 1];
                for (int h = 1; h <= cap; h++)
                {
                    float a = Harmonic(v, h);
                    for (int i = 0; i < TableSize; i++)
                        t[i] += a * MathF.Sin(Dsp.TwoPi * h * i / TableSize);
                }
                double e = 0;
                for (int i = 0; i < TableSize; i++) e += t[i] * t[i];
                float rms = (float)Math.Sqrt(e / TableSize);
                float scale = rms > 1e-9f ? 0.30f / rms : 0f;
                for (int i = 0; i < TableSize; i++) t[i] *= scale;
                t[TableSize] = t[0];
                all[v][lv] = t;
            }
        }
        return all;
    }

    // ---- parameters ----
    readonly EffectParameter voiceP, octP, toneP, sustainP, growlP, levelP, dryP;

    // ---- state (audio thread only) ----
    readonly PitchTracker tracker = new();
    readonly Biquad bodyLp = new(), postLp = new();
    EnvFollower env;
    float sr = 48000f;
    float phase, freqCur, ampCur;
    float atkC, relC, glideC;
    bool gateOpen, wasOn;
    int lastVoice = -1;

    // ---- readout (thread-safe snapshots for the UI) ----
    volatile int noteOut = -1;
    volatile float freqOut;
    volatile bool voicedOut;

    public string Headline => voicedOut && noteOut >= 0 ? NoteNames[noteOut % 12] + (noteOut / 12 - 1) : "--";
    public string Detail => voicedOut
        ? $"{VoiceNames[Math.Clamp((int)(voiceP.Value + 0.5f), 0, VoiceNames.Length - 1)]} bass   {freqOut:F1} Hz"
        : "play a single note";
    public bool HasNeedle => false;
    public float Needle => 0f;
    public bool HasProgress => false;
    public float Progress => 0f;

    public BassSynth() : base("Bass Synth")
    {
        voiceP = Add("voice", "Voice", 0f, VoiceNames.Length - 1, 0f, "", ParameterCurve.Linear, 0);
        voiceP.Labels = VoiceNames;
        octP = Add("octave", "Octave", 0f, OctaveNames.Length - 1, 0f, "", ParameterCurve.Linear, 0);
        octP.Labels = OctaveNames;
        toneP = Add("tone", "Tone", 0f, 1f, 0.5f, "", ParameterCurve.Linear, 2);
        sustainP = Add("sustain", "Sustain", 0f, 100f, 30f, " %", ParameterCurve.Linear, 0);
        growlP = Add("growl", "Growl", 0f, 100f, 15f, " %", ParameterCurve.Linear, 0);
        levelP = Add("level", "Level", -12f, 12f, 0f, " dB", ParameterCurve.Linear, 1);
        dryP = Add("dry", "Dry", 0f, 100f, 0f, " %", ParameterCurve.Linear, 0);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        tracker.Prepare(sr);
        env.Set(1f, 50f, sr); env.Reset();
        bodyLp.Lowpass(800f, 0.707f, sr); bodyLp.Reset();
        postLp.Lowpass(3000f, 0.707f, sr); postLp.Reset();
        phase = 0f; freqCur = 0f; ampCur = 0f;
        glideC = Dsp.TimeToCoef(12f, sr);
        gateOpen = false; wasOn = false;
        lastVoice = -1;                      // forces SetVoice on the first block
        noteOut = -1; voicedOut = false; freqOut = 0f;
    }

    void SetVoice(int voice)
    {
        atkC = Dsp.TimeToCoef(Voices[voice].AttackMs, sr);
        relC = Dsp.TimeToCoef(Voices[voice].ReleaseMs, sr);
        lastVoice = voice;
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        int vi = Math.Clamp((int)(voiceP.Value + 0.5f), 0, VoiceNames.Length - 1);
        if (vi != lastVoice) SetVoice(vi);
        Voice v = Voices[vi];
        float[][] mips = Tables[vi];

        float octMul = octP.Value >= 0.5f ? 0.25f : 0.5f;           // relative to the guitar note
        float toneMul = Dsp.ExpMap(toneP.Value, 0.5f, 2f);          // 0.5 = neutral
        float shape = 1f - 0.5f * sustainP.Value * 0.01f;           // 1 = follows the string, 0.5 = sqrt (held tail)
        float drive = 1f + growlP.Value * 0.06f;
        float driveNorm = 1f / Dsp.Tanh(drive);
        float outG = Dsp.DbToLin(levelP.Value);
        float dryG = dryP.Value * 0.01f;

        float[] tbl = mips[0];
        int ctl = 0;

        for (int i = 0; i < n; i++)
        {
            float x = buf[offset + i];
            tracker.Process(x);

            float e = env.Next(x);
            if (gateOpen)
            {
                if (e < GateOff) gateOpen = false;
            }
            else if (e > GateOn)
            {
                gateOpen = true;
                tracker.Unlock();           // fresh, immediately accepted pitch lock for the new note
            }

            bool on = gateOpen && tracker.Locked && tracker.Frequency > 0f;
            float lvl = MathF.Min(1f, e * 6f);
            float target = on ? MathF.Pow(lvl, shape) : 0f;
            ampCur = target + (ampCur - target) * (target > ampCur ? atkC : relC);

            if (on)
            {
                float tf = Dsp.Clamp(tracker.Frequency * octMul, 20f, 1000f);
                if (!wasOn || freqCur <= 0f || MathF.Abs(tf - freqCur) > 0.03f * freqCur) freqCur = tf;
                else freqCur = tf + (freqCur - tf) * glideC;
            }
            wasOn = on;

            if ((ctl++ & 31) == 0)
            {
                float f = freqCur > 0f ? freqCur : 50f;
                float allowed = 0.45f * sr / f;
                int lv = 0;
                while (lv < Levels - 1 && (TopHarmonics >> lv) > allowed) lv++;
                tbl = mips[lv];
                bodyLp.Lowpass((v.BaseCut + v.DynCut * ampCur) * toneMul, v.Q, sr);
                postLp.Lowpass(Dsp.Clamp(1800f * toneMul + 1200f, 600f, 6000f), 0.707f, sr);
            }

            float o = 0f;
            if (ampCur > 1e-5f)
            {
                phase += freqCur / sr;
                if (phase >= 1f) phase -= 1f;
                float p = phase * TableSize;
                int ix = Math.Min((int)p, TableSize - 1);
                float fr = p - ix;
                o = tbl[ix] + (tbl[ix + 1] - tbl[ix]) * fr;
            }

            float body = bodyLp.Process(o);
            body = Dsp.Tanh(body * drive) * driveNorm;      // growl: soft saturation, adds harmonics on loud notes
            body = postLp.Process(body);

            buf[offset + i] = Dsp.SoftLimit(x * dryG + body * ampCur * outG * 0.6f);
        }

        voicedOut = gateOpen && tracker.Locked;
        if (voicedOut && freqCur > 0f)
        {
            freqOut = freqCur;
            noteOut = Math.Max(0, (int)MathF.Round(69f + 12f * MathF.Log2(freqCur / 440f)));
        }
    }
}
