using System;
using System.Diagnostics;
using System.Threading;

namespace Jrig.Effects;

/// <summary>
/// Synthesised drum backing that plays along with your guitar. Your guitar passes through untouched and the
/// drums are added on top. Everything is made from oscillators and noise (no samples), so there is nothing to load.
///
/// Style picks the pattern, Tempo the speed, Swing pushes the off-beat 16ths late, Tone brightens the kit,
/// Decay makes the drums tighter or boomier. The footswitch starts and stops the drums (switch the pedal on and the
/// drums start on beat 1). RESTART jumps back to beat 1, FILL plays a tom fill in the second half of the bar.
/// </summary>
public sealed class DrumMachine : EffectBase, IPedalReadout, IPedalActions, IExemptFromBypassAll
{
    // ------------------------------------------------------------------ styles
    // Each row is one step per character: '.' = no hit, '1'..'9' = hit with that velocity.
    // Steps = 16 means 16th notes (4 per beat). Steps = 12 means triplets (3 per beat) for the shuffle feels.
    sealed class DrumStyle
    {
        public readonly string Name;
        public readonly int Steps;
        public readonly string Kick, Snare, Hat, Open, Rim;
        public DrumStyle(string name, int steps, string kick, string snare, string hat, string open, string rim)
        {
            Name = name; Steps = steps; Kick = kick; Snare = snare; Hat = hat; Open = open; Rim = rim;
        }
    }

    static readonly DrumStyle[] Styles =
    {
        //                                  kick               snare              closed hat          open hat            rim / clap
        new("Metronome", 16,  "",                 "",                 "",                 "",                 "9...5...5...5..."),
        new("Rock",      16,  "9.......9.9.....", "....9.......9...", "8.5.8.5.8.5.8.5.", "",                 ""),
        new("Pop",       16,  "9.....9.9.......", "....9.......9...", "7.7.7.7.7.7.7.7.", "",                 ""),
        new("Disco",     16,  "9...9...9...9...", "....9.......9...", "6...6...6...6...", "..8...8...8...8.", "....8.......8..."),
        new("Funk",      16,  "9..9..9...9..9..", "....9..4.4..9..4", "8575857585758575", "",                 ""),
        new("Shuffle",   12,  "9.....9.....",     "...9.....9..",     "8.68.68.68.6",     "",                 ""),
        new("Slow Blues",12,  "9.....7.9...",     "...9.....9..",     "745745745745",     "",                 ""),
        new("Half-Time", 16,  "9.........9.....", "........9.......", "7.7.7.7.7.7.7.7.", "",                 ""),
        new("Metal",     16,  "9.9.9.9.9.9.9.9.", "....9.......9...", "8...8...8...8...", "",                 ""),
        new("Reggae",    16,  "........9.......", "",                 "5.8.5.8.5.8.5.8.", "",                 "........9......."),
        new("Bossa Nova",16,  "9.....9.9.....9.", "",                 "6.6.6.6.6.6.6.6.", "",                 "9..9..9...9.9..."),
        new("Hip-Hop",   16,  "9......9..9.....", "....9.......9...", "7.7.7.7.7.7.7.7.", "",                 ""),
    };

    static readonly string[] StyleNames = Array.ConvertAll(Styles, s => s.Name);

    // Instrument ids for Trigger().
    const int Kick = 0, Snare = 1, Hat = 2, Open = 3, Rim = 4, TomHi = 5, TomMid = 6, TomLow = 7;
    const int CmdNone = 0, CmdRestart = 1, CmdFill = 2;

    // ------------------------------------------------------------------ parameters
    readonly EffectParameter style, tempo, level, tone, swing, decay;

    // ------------------------------------------------------------------ sequencer (audio thread only)
    int step;                  // current step in the bar
    float stepPos;             // samples since the current step began
    bool fired;                // has the current step's hits been played yet
    bool fillArmed, fillActive;
    long lastTicks;            // when ProcessCore last ran: a long gap means the pedal was switched off, so restart on beat 1

    int pending;               // UI -> audio thread command
    volatile int styleOut;     // audio thread -> UI
    volatile float progressOut;
    volatile float bpmOut = 100f;
    volatile int beatOut = 1;
    volatile bool fillOut;

    // ------------------------------------------------------------------ voices (audio thread only)
    float sr = 48000f, invSr = 1f / 48000f;
    uint rng = 22222u;

    // kick
    float kEnv, kPitch, kClick, kPh;
    // snare
    float sEnvT, sEnvN, sPh;
    // hats (closed and open share the metallic oscillators)
    float hEnv, oEnv;
    readonly float[] hatPh = new float[6];
    static readonly float[] HatFreq = { 205.3f, 304.4f, 369.6f, 522.7f, 540.0f, 800.0f };
    // rim / clap
    float rEnv, rPh;
    // toms
    readonly float[] tEnv = new float[3], tPitch = new float[3], tPh = new float[3];
    static readonly float[] TomBase = { 205f, 150f, 108f };

    // per-sample decay multipliers (recomputed when Decay changes)
    float kAmpC, kPitC, kClkC, sToneC, sNoiseC, hC, oC, rC, tAmpC, tPitC;
    float lastDecay = -1f, lastTone = -1f;

    readonly Biquad snareHp = new(), hatHp = new(), rimBp = new(), busLp = new();
    Smooth levelS;

    public DrumMachine() : base("Drum Machine")
    {
        style = Add("style", "Style", 0f, Styles.Length - 1, 1f, "", ParameterCurve.Linear, 0);
        style.Labels = StyleNames;
        tempo = Add("tempo", "Tempo", 40f, 220f, 100f, " BPM", ParameterCurve.Linear, 0);
        level = Add("level", "Level", -30f, 6f, -6f, " dB", ParameterCurve.Linear, 1);
        tone = Add("tone", "Tone", 0f, 1f, 0.5f, "", ParameterCurve.Linear, 2);
        swing = Add("swing", "Swing", 0f, 100f, 0f, " %", ParameterCurve.Linear, 0);
        decay = Add("decay", "Decay", 50f, 200f, 100f, " %", ParameterCurve.Linear, 0);

        Actions = new[]
        {
            new PedalAction("RESTART", () => Volatile.Write(ref pending, CmdRestart)),
            new PedalAction("FILL", () => Volatile.Write(ref pending, CmdFill)),
        };
    }

    public IReadOnlyList<PedalAction> Actions { get; }

    // ------------------------------------------------------------------ readout (UI thread)
    public string Headline => StyleNames[Math.Clamp(styleOut, 0, StyleNames.Length - 1)];
    public string Detail => $"{bpmOut:F0} BPM   beat {beatOut}" + (fillOut ? "   FILL" : "");
    public bool HasNeedle => false;
    public float Needle => 0f;
    public bool HasProgress => true;
    public float Progress => progressOut;

    // ------------------------------------------------------------------ setup
    protected override void OnPrepare()
    {
        sr = SampleRate;
        invSr = 1f / sr;
        levelS.Init(Dsp.DbToLin(level.Value), 15f, sr);
        lastDecay = -1f;
        lastTone = -1f;
        rimBp.Bandpass(2400f, 1.5f, sr);
        ResetState();
    }

    void ResetState()
    {
        step = 0; stepPos = 0f; fired = false;
        fillArmed = false; fillActive = false;
        kEnv = kPitch = kClick = kPh = 0f;
        sEnvT = sEnvN = sPh = 0f;
        hEnv = oEnv = 0f;
        rEnv = rPh = 0f;
        for (int i = 0; i < 6; i++) hatPh[i] = 0f;
        for (int i = 0; i < 3; i++) { tEnv[i] = 0f; tPitch[i] = 0f; tPh[i] = 0f; }
        snareHp.Reset(); hatHp.Reset(); rimBp.Reset(); busLp.Reset();
        lastTicks = 0;
        progressOut = 0f;
        beatOut = 1;
        fillOut = false;
    }

    void UpdateDecay(float d)
    {
        float k = d * 0.01f;                           // 0.5 .. 2.0
        kAmpC = Dsp.TimeToCoef(85f * k, sr);
        kPitC = Dsp.TimeToCoef(22f, sr);               // pitch drop stays fast so the kick keeps its thump
        kClkC = Dsp.TimeToCoef(2f, sr);
        sToneC = Dsp.TimeToCoef(38f * k, sr);
        sNoiseC = Dsp.TimeToCoef(55f * k, sr);
        hC = Dsp.TimeToCoef(16f * k, sr);
        oC = Dsp.TimeToCoef(120f * k, sr);
        rC = Dsp.TimeToCoef(13f * k, sr);
        tAmpC = Dsp.TimeToCoef(95f * k, sr);
        tPitC = Dsp.TimeToCoef(45f, sr);
        lastDecay = d;
    }

    void UpdateTone(float t)
    {
        snareHp.Highpass(Dsp.ExpMap(t, 1200f, 3500f), 0.707f, sr);
        hatHp.Highpass(Dsp.ExpMap(t, 5500f, 9000f), 0.707f, sr);
        busLp.Lowpass(Dsp.ExpMap(t, 3000f, 14000f), 0.707f, sr);
        lastTone = t;
    }

    // ------------------------------------------------------------------ sound
    float Noise()
    {
        rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
        return (rng & 0xFFFFFFu) * (1f / 8388608f) - 1f;
    }

    void Trigger(int inst, float vel)
    {
        switch (inst)
        {
            case Kick: kEnv = vel; kPitch = 1f; kClick = vel; kPh = 0f; break;
            case Snare: sEnvT = vel; sEnvN = vel; sPh = 0f; break;
            case Hat: hEnv = vel; oEnv *= 0.15f; break;       // a closed hat chokes a ringing open hat
            case Open: oEnv = vel; break;
            case Rim: rEnv = vel; rPh = 0f; break;
            case TomHi: case TomMid: case TomLow:
                int t = inst - TomHi;
                tEnv[t] = vel; tPitch[t] = 1f; tPh[t] = 0f;
                break;
        }
    }

    float RenderSample(float toneAmt)
    {
        float d = 0f;

        if (kEnv > 1e-4f)
        {
            kPh += (48f + 120f * kPitch) * invSr;
            if (kPh >= 1f) kPh -= 1f;
            d += Lfo.Sine(kPh) * kEnv * 1.1f + Noise() * kClick * (0.12f + 0.3f * toneAmt);
            kEnv *= kAmpC; kPitch *= kPitC; kClick *= kClkC;
        }

        if (sEnvT > 1e-4f || sEnvN > 1e-4f)
        {
            sPh += (180f + 90f * sEnvT) * invSr;
            if (sPh >= 1f) sPh -= 1f;
            d += Lfo.Sine(sPh) * sEnvT * 0.55f + snareHp.Process(Noise()) * sEnvN * 0.85f;
            sEnvT *= sToneC; sEnvN *= sNoiseC;
        }

        if (hEnv > 1e-4f || oEnv > 1e-4f)
        {
            float m = 0f;
            for (int i = 0; i < 6; i++)
            {
                float p = hatPh[i] + HatFreq[i] * invSr;
                if (p >= 1f) p -= 1f;
                hatPh[i] = p;
                m += p < 0.5f ? 1f : -1f;
            }
            d += hatHp.Process(m * 0.16f) * (hEnv + oEnv) * 0.5f;
            hEnv *= hC; oEnv *= oC;
        }

        if (rEnv > 1e-4f)
        {
            rPh += 1000f * invSr;
            if (rPh >= 1f) rPh -= 1f;
            d += (Lfo.Sine(rPh) * 0.45f + rimBp.Process(Noise()) * 0.8f) * rEnv;
            rEnv *= rC;
        }

        for (int t = 0; t < 3; t++)
        {
            if (tEnv[t] > 1e-4f)
            {
                tPh[t] += TomBase[t] * (1f + 0.5f * tPitch[t]) * invSr;
                if (tPh[t] >= 1f) tPh[t] -= 1f;
                d += Lfo.Sine(tPh[t]) * tEnv[t] * 0.85f;
                tEnv[t] *= tAmpC; tPitch[t] *= tPitC;
            }
        }

        return d;
    }

    // ------------------------------------------------------------------ sequencer
    static float Vel(string row, int s) =>
        s < row.Length && row[s] >= '1' && row[s] <= '9' ? (row[s] - '0') / 9f : 0f;

    void FireStep(DrumStyle st)
    {
        if (fillArmed && step >= st.Steps / 2) { fillArmed = false; fillActive = true; }

        if (fillActive && step >= st.Steps / 2)
        {
            FireFill(st.Steps);
            return;
        }

        float v;
        if ((v = Vel(st.Kick, step)) > 0f) Trigger(Kick, v);
        if ((v = Vel(st.Snare, step)) > 0f) Trigger(Snare, v);
        if ((v = Vel(st.Hat, step)) > 0f) Trigger(Hat, v);
        if ((v = Vel(st.Open, step)) > 0f) Trigger(Open, v);
        if ((v = Vel(st.Rim, step)) > 0f) Trigger(Rim, v);
    }

    /// <summary>A short snare-and-tom run that fills the second half of the bar.</summary>
    void FireFill(int steps)
    {
        if (steps == 16)
        {
            switch (step)
            {
                case 8: Trigger(Snare, 0.6f); break;
                case 10: Trigger(Snare, 0.75f); break;
                case 12: Trigger(TomHi, 0.9f); break;
                case 13: Trigger(TomHi, 0.75f); break;
                case 14: Trigger(TomMid, 0.9f); break;
                case 15: Trigger(TomLow, 1f); break;
            }
        }
        else
        {
            switch (step)
            {
                case 6: Trigger(Snare, 0.6f); break;
                case 8: Trigger(TomHi, 0.9f); break;
                case 9: Trigger(TomMid, 0.9f); break;
                case 10: Trigger(TomMid, 0.8f); break;
                case 11: Trigger(TomLow, 1f); break;
            }
        }
    }

    // Drums rendered ahead of time for the looper (see EffectChain.Process); ProcessCore then just adds them.
    readonly float[] bus = new float[Jrig.Audio.AudioEngine.MaxBlock];
    int busCount;
    bool busReady;

    /// <summary>The drums-only block from the last <see cref="PreRender"/> call.</summary>
    public float[] Bus => bus;

    /// <summary>Audio thread. Renders this block's drums into <see cref="Bus"/> so a looper placed earlier on the
    /// board can record them. Returns false when the pedal is off or the block is too large.</summary>
    public bool PreRender(int count)
    {
        if (Bypassed || count > bus.Length) return false;
        RenderBlock(bus, 0, count, false);
        busCount = count;
        busReady = true;
        return true;
    }

    protected override void ProcessCore(float[] buffer, int offset, int count)
    {
        if (busReady && busCount == count)
        {
            busReady = false;
            for (int i = 0; i < count; i++) buffer[offset + i] += bus[i];
            return;
        }
        busReady = false;
        RenderBlock(buffer, offset, count, true);
    }

    void RenderBlock(float[] buffer, int offset, int count, bool add)
    {
        long now = Stopwatch.GetTimestamp();
        if (lastTicks != 0 && now - lastTicks > Stopwatch.Frequency / 10)
            ResetState();                                  // the pedal was off for a while: start again on beat 1

        int cmd = Interlocked.Exchange(ref pending, CmdNone);
        if (cmd == CmdRestart) { step = 0; stepPos = 0f; fired = false; fillArmed = false; fillActive = false; }
        else if (cmd == CmdFill) fillArmed = true;

        int si = Math.Clamp((int)(style.Value + 0.5f), 0, Styles.Length - 1);
        DrumStyle st = Styles[si];
        if (step >= st.Steps) { step = 0; stepPos = 0f; fired = false; fillActive = false; }

        float bpm = tempo.Value;
        float stepLen = sr * 60f / (bpm * (st.Steps == 16 ? 4f : 3f));
        float swingFrac = st.Steps == 16 ? swing.Value * 0.01f * 0.34f : 0f;   // 100 % = full triplet swing
        float toneAmt = tone.Value;
        if (decay.Value != lastDecay) UpdateDecay(decay.Value);
        if (toneAmt != lastTone) UpdateTone(toneAmt);
        float lvlTarget = Dsp.DbToLin(level.Value);

        for (int i = 0; i < count; i++)
        {
            if (stepPos >= stepLen)
            {
                stepPos -= stepLen;
                fired = false;
                if (++step >= st.Steps) { step = 0; fillActive = false; }
            }

            if (!fired && stepPos >= ((step & 1) == 1 ? swingFrac * stepLen : 0f))
            {
                fired = true;
                FireStep(st);
            }
            stepPos += 1f;

            float drums = Dsp.SoftLimit(busLp.Process(RenderSample(toneAmt))) * levelS.Next(lvlTarget);
            if (add) buffer[offset + i] += drums; else buffer[offset + i] = drums;
        }

        styleOut = si;
        bpmOut = bpm;
        beatOut = step * 4 / st.Steps + 1;
        progressOut = (step + Math.Min(stepPos / stepLen, 1f)) / st.Steps;
        fillOut = fillArmed || fillActive;
        lastTicks = Stopwatch.GetTimestamp();
    }
}
