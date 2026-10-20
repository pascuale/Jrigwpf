using System;

namespace Jrig.Effects;

/// <summary>
/// Everything that makes one tube amp sound different from another: the controls it has,
/// how much gain each stage adds, where the tone stack sits, how the power section sags,
/// and what speaker cabinet it is wearing. See the amp classes at the bottom of this file.
/// </summary>
internal sealed class AmpVoicing
{
    // Which knobs exist. Gain and Level are always present.
    public string GainName = "Volume";
    public string TrebleName = "Treble";
    public bool HasBass = true, HasMid = true, HasTreble = true;
    public bool HasPresence, HasMaster, HasReverb, HasBright, HasContour;

    // Preamp.
    public float InputHpHz = 40f;        // tightens the low end going into the first tube
    public float PreLpHz = 9000f;        // tames fizz / aliasing before the clipper
    public float StageADb = 6f;          // fixed gain of the first tube
    public float KnobMinDb = -30f;       // what the Gain knob does: dB at 0 ...
    public float KnobMaxDb = 12f;        // ... and at 10
    public int ExtraStages = 0;          // extra gain tubes after the Gain knob (0..2)
    public float StageBDb = 10f, StageCDb = 8f;
    public float Bias = 0.15f;           // tube asymmetry (more = more even harmonics, "fuzzier")
    public float CouplingHz = 60f;       // coupling-cap highpass between stages

    // Passive tone stack: bands only cut, then a recovery stage makes the gain back up.
    public float StackLowHz = 100f, StackHighHz = 2500f;
    public float BandRangeDb = 22f;      // how far a pot at 0 cuts its band
    public float MidBaseDb = 0f;         // fixed mid dip (Fender stacks are scooped)
    public float ContourMaxDb = 14f;     // extra mid scoop at Contour 10
    public float RecoveryDb = 10f;
    public float BrightHz = 1800f;       // bright cap corner

    // Power amp.
    public float PowerMaxDb = 0f;        // drive into the power tubes (Master at 10, or fixed if no Master)
    public float Sag = 0.1f;             // supply sag: gain drops after loud notes
    public float TransformerHpHz = 50f, TransformerLpHz = 9000f;
    public float PresenceHz = 3500f, PresenceMax = 0.9f, FixedPresence = 0.15f;

    // Speaker cabinet.
    public float CabHpHz = 80f, CabLpHz = 5000f, CabPeakHz = 2500f, CabPeakQ = 1.2f, CabPeakGain = 0.6f;

    // Output trim so the amps sit at similar loudness at default settings.
    public float OutTrimDb = 0f;
    public float DefaultGain = 5f, DefaultMaster = 5f;   // knob positions (0..10) the amp starts at

    // Spring reverb feedback and damping.
    public float ReverbFeedback = 0.72f, ReverbDamp = 0.45f;
}

/// <summary>A small Schroeder-style reverb voiced dark and boingy, standing in for a spring tank.</summary>
internal sealed class SpringReverb
{
    readonly DelayLine[] comb = { new(), new(), new(), new() };
    readonly DelayLine[] ap = { new(), new() };
    readonly float[] combLen = new float[4];
    readonly float[] apLen = new float[2];
    readonly float[] damp = new float[4];
    float fb = 0.72f, dampCoef = 0.45f;

    static readonly float[] CombMs = { 29.7f, 37.1f, 41.1f, 43.7f };
    static readonly float[] ApMs = { 5.0f, 1.7f };

    public void Prepare(float sr, float feedback, float dampening)
    {
        fb = feedback; dampCoef = dampening;
        for (int i = 0; i < 4; i++)
        {
            combLen[i] = CombMs[i] * 0.001f * sr;
            comb[i].Allocate((int)combLen[i] + 4);
            damp[i] = 0f;
        }
        for (int i = 0; i < 2; i++)
        {
            apLen[i] = ApMs[i] * 0.001f * sr;
            ap[i].Allocate((int)apLen[i] + 4);
        }
    }

    public float Process(float x)
    {
        float sum = 0f;
        for (int i = 0; i < 4; i++)
        {
            float d = comb[i].ReadLinear(combLen[i]);
            damp[i] = Dsp.Flush(d * (1f - dampCoef) + damp[i] * dampCoef);
            comb[i].Write(x + damp[i] * fb);
            sum += d;
        }
        sum *= 0.25f;
        for (int i = 0; i < 2; i++)
        {
            float v = ap[i].ReadLinear(apLen[i]);
            float w = sum + 0.55f * v;
            ap[i].Write(w);
            sum = v - 0.55f * w;
        }
        return sum;
    }
}

/// <summary>
/// Shared tube-amp engine. Signal path, left to right:
/// input highpass (+ bright cap) -> tube A -> Gain knob -> extra tubes -> passive tone stack
/// -> recovery tube -> (spring reverb) -> Master -> power tubes with sag -> presence -> output transformer
/// -> speaker cabinet -> level.
/// Knobs are marked 0..10 like the real amps.
/// </summary>
public abstract class TubeAmp : EffectBase
{
    readonly AmpVoicing v;
    readonly EffectParameter gain, level, cab;
    readonly EffectParameter? bass, mid, treble, presence, master, reverb, bright, contour;

    readonly Biquad inHp = new(), brightHp = new(), preLp = new();
    readonly Biquad cplA = new(), cplB = new(), cplC = new();
    readonly Biquad stackLo = new(), stackHi = new();
    readonly Biquad presHp = new(), trHp = new(), trLp = new();
    readonly Biquad cabHp = new(), cabLp = new(), cabPeak = new(), dc = new();
    readonly SpringReverb verb = new();
    EnvFollower sag;
    Smooth gainS, bassS, midS, trebS, presS, mastS, revS, brightS, cabS, levelS;
    float sr = 48000f;

    internal TubeAmp(string name, AmpVoicing voicing) : base(name)
    {
        v = voicing;
        gain = Add("gain", v.GainName, 0f, 10f, v.DefaultGain, "", ParameterCurve.Linear, 1);
        if (v.HasBass) bass = Add("bass", "Bass", 0f, 10f, 5f, "", ParameterCurve.Linear, 1);
        if (v.HasMid) mid = Add("mid", "Middle", 0f, 10f, 5f, "", ParameterCurve.Linear, 1);
        if (v.HasTreble) treble = Add("treble", v.TrebleName, 0f, 10f, 5f, "", ParameterCurve.Linear, 1);
        if (v.HasPresence) presence = Add("presence", "Presence", 0f, 10f, 5f, "", ParameterCurve.Linear, 1);
        if (v.HasContour) contour = Add("contour", "Contour", 0f, 10f, 0f, "", ParameterCurve.Linear, 1);
        if (v.HasMaster) master = Add("master", "Master", 0f, 10f, v.DefaultMaster, "", ParameterCurve.Linear, 1);
        if (v.HasBright) bright = Add("bright", "Bright", 0f, 10f, 0f, "", ParameterCurve.Linear, 1);
        if (v.HasReverb) reverb = Add("reverb", "Reverb", 0f, 10f, 2f, "", ParameterCurve.Linear, 1);
        cab = Add("cab", "Cab", 0f, 100f, 100f, " %", ParameterCurve.Linear, 0);
        level = Add("level", "Level", -24f, 12f, 0f, " dB", ParameterCurve.Linear, 1);
    }

    static float N(EffectParameter? p, float fallback) => p == null ? fallback : p.Value * 0.1f;

    // ---- control mappings (each returns a linear gain) ----
    float GainLin(float n) => Dsp.DbToLin(v.KnobMinDb + (v.KnobMaxDb - v.KnobMinDb) * n);
    float BandLin(float n, float extraDb) => Dsp.DbToLin(-v.BandRangeDb * (1f - n) + extraDb);
    float MasterLin(float n) => v.HasMaster
        ? Dsp.DbToLin(-30f + (30f + v.PowerMaxDb) * n)
        : Dsp.DbToLin(v.PowerMaxDb);

    protected override void OnPrepare()
    {
        sr = SampleRate;
        inHp.Highpass(v.InputHpHz, 0.707f, sr);
        brightHp.Highpass(v.BrightHz, 0.707f, sr);
        preLp.Lowpass(v.PreLpHz, 0.707f, sr);
        cplA.Highpass(v.CouplingHz, 0.707f, sr);
        cplB.Highpass(v.CouplingHz * 1.5f, 0.707f, sr);
        cplC.Highpass(v.CouplingHz * 2f, 0.707f, sr);
        stackLo.Lowpass(v.StackLowHz, 0.707f, sr);
        stackHi.Highpass(v.StackHighHz, 0.707f, sr);
        presHp.Highpass(v.PresenceHz, 0.707f, sr);
        trHp.Highpass(v.TransformerHpHz, 0.707f, sr);
        trLp.Lowpass(v.TransformerLpHz, 0.707f, sr);
        cabHp.Highpass(v.CabHpHz, 0.707f, sr);
        cabLp.Lowpass(v.CabLpHz, 0.9f, sr);
        cabPeak.Bandpass(v.CabPeakHz, v.CabPeakQ, sr);
        dc.Highpass(20f, 0.707f, sr);
        foreach (var f in new[] { inHp, brightHp, preLp, cplA, cplB, cplC, stackLo, stackHi, presHp, trHp, trLp, cabHp, cabLp, cabPeak, dc })
            f.Reset();

        sag.Set(20f, 250f, sr);
        sag.Reset();
        if (v.HasReverb) verb.Prepare(sr, v.ReverbFeedback, v.ReverbDamp);

        InitSmooth();
    }

    void InitSmooth()
    {
        float g = N(gain, 0.5f);
        gainS.Init(GainLin(g), 15f, sr);
        bassS.Init(BandLin(N(bass, 0.5f), 0f), 15f, sr);
        midS.Init(MidTarget(), 15f, sr);
        trebS.Init(BandLin(N(treble, 0.5f), 0f), 15f, sr);
        presS.Init(PresenceTarget(), 15f, sr);
        mastS.Init(MasterLin(N(master, 0.5f)), 15f, sr);
        revS.Init(ReverbTarget(), 15f, sr);
        brightS.Init(BrightTarget(), 15f, sr);
        cabS.Init(cab.Value * 0.01f, 15f, sr);
        levelS.Init(Dsp.DbToLin(level.Value + v.OutTrimDb), 15f, sr);
    }

    float MidTarget()
    {
        float scoop = v.HasContour ? -v.ContourMaxDb * N(contour, 0f) : 0f;
        return BandLin(N(mid, 0.5f), v.MidBaseDb + scoop);
    }
    float PresenceTarget() => v.HasPresence ? v.PresenceMax * N(presence, 0.5f) : v.FixedPresence;
    float ReverbTarget() { float r = N(reverb, 0f); return r * r * 0.9f; }
    // Bright cap: strongest with Volume low, fades as Volume rises (like the real cap across the pot).
    float BrightTarget() => v.HasBright ? N(bright, 0f) * (1f - N(gain, 0.5f)) * 1.5f : 0f;

    /// <summary>One gain tube: asymmetric soft clip, normalised so small signals pass at unity.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static float Tube(float x, float bias, float biasTanh) => Dsp.Tanh(x + bias) - biasTanh;

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float bias = v.Bias, biasT = Dsp.Tanh(bias);
        float aLin = Dsp.DbToLin(v.StageADb), bLin = Dsp.DbToLin(v.StageBDb), cLin = Dsp.DbToLin(v.StageCDb);
        float recLin = Dsp.DbToLin(v.RecoveryDb);

        // Block-rate targets; the Smooth structs glide to them per sample.
        float gT = GainLin(N(gain, 0.5f));
        float bsT = BandLin(N(bass, 0.5f), 0f), mdT = MidTarget(), trT = BandLin(N(treble, 0.5f), 0f);
        float prT = PresenceTarget(), msT = MasterLin(N(master, 0.5f)), rvT = ReverbTarget(), brT = BrightTarget();
        float cbT = cab.Value * 0.01f, lvT = Dsp.DbToLin(level.Value + v.OutTrimDb);
        bool useVerb = v.HasReverb;
        int extra = v.ExtraStages;

        for (int i = 0; i < n; i++)
        {
            float x = buf[offset + i];

            // ---- preamp ----
            float s = inHp.Process(x);
            float br = brightS.Next(brT);
            if (br > 0.0005f) s += br * brightHp.Process(s);
            s = preLp.Process(s);

            s = Tube(s * aLin, bias, biasT);
            s = cplA.Process(s);
            s *= gainS.Next(gT);
            if (extra >= 1) { s = Tube(s * bLin, bias, biasT); s = cplB.Process(s); }
            if (extra >= 2) { s = Tube(s * cLin, bias, biasT); s = cplC.Process(s); }

            // ---- tone stack (three bands that can only cut) ----
            float lo = stackLo.Process(s);
            float hi = stackHi.Process(s);
            float md = s - lo - hi;
            s = lo * bassS.Next(bsT) + md * midS.Next(mdT) + hi * trebS.Next(trT);
            s = Tube(s * recLin, bias * 0.5f, Dsp.Tanh(bias * 0.5f));

            // ---- spring reverb (wet is added before the power stage, so it breaks up with the amp) ----
            if (useVerb) s += verb.Process(s) * revS.Next(rvT);

            // ---- power amp ----
            float p = s * mastS.Next(msT);
            float pg = 1f - v.Sag * Dsp.Clamp(sag.Value, 0f, 1f);
            p = Dsp.Tanh(p * pg);
            sag.Next(p);
            p += presHp.Process(p) * presS.Next(prT);
            p = trLp.Process(trHp.Process(p));

            // ---- speaker cabinet ----
            float c = cabLp.Process(cabHp.Process(p)) + cabPeak.Process(p) * v.CabPeakGain;
            float cm = cabS.Next(cbT);
            float y = p + (c - p) * cm;

            buf[offset + i] = Dsp.SoftLimit(dc.Process(y)) * levelS.Next(lvT);
        }
    }
}

/// <summary>Marshall JCM800-style 100 W head into a 4x12 stack. Cascaded gain, tight and cutting.</summary>
public sealed class MarshallStack : TubeAmp
{
    public MarshallStack() : base("Marshall Stack", new AmpVoicing
    {
        GainName = "Preamp", HasPresence = true, HasMaster = true,
        InputHpHz = 80f, PreLpHz = 9000f,
        DefaultGain = 6f, DefaultMaster = 6f,
        StageADb = 9f, KnobMinDb = -24f, KnobMaxDb = 12f, ExtraStages = 1, StageBDb = 9f, Bias = 0.2f,
        StackLowHz = 110f, StackHighHz = 2600f, BandRangeDb = 22f, MidBaseDb = 0f, RecoveryDb = 10f,
        PowerMaxDb = 12f, Sag = 0.25f, TransformerHpHz = 50f, TransformerLpHz = 9000f,
        PresenceHz = 3500f, PresenceMax = 0.9f,
        CabHpHz = 80f, CabLpHz = 4800f, CabPeakHz = 2400f, CabPeakQ = 1.2f, CabPeakGain = 0.7f,
        OutTrimDb = -2.5f,
    }) { }
}

/// <summary>Fender Twin Reverb (blackface): huge clean headroom, scooped mids, sparkly highs, spring reverb.</summary>
public sealed class FenderTwinReverb : TubeAmp
{
    public FenderTwinReverb() : base("Fender Twin Reverb", new AmpVoicing
    {
        GainName = "Volume", HasBright = true, HasReverb = true,
        InputHpHz = 30f, PreLpHz = 12000f,
        StageADb = 6f, KnobMinDb = -40f, KnobMaxDb = 8f, ExtraStages = 0, Bias = 0.1f,
        StackLowHz = 90f, StackHighHz = 2800f, BandRangeDb = 24f, MidBaseDb = -6f, RecoveryDb = 11f,
        BrightHz = 2000f,
        PowerMaxDb = 12f, Sag = 0.08f, TransformerHpHz = 40f, TransformerLpHz = 11000f, FixedPresence = 0.2f,
        CabHpHz = 85f, CabLpHz = 6000f, CabPeakHz = 3300f, CabPeakQ = 1.0f, CabPeakGain = 0.4f,
        OutTrimDb = 8f, ReverbFeedback = 0.72f, ReverbDamp = 0.45f,
    }) { }
}

/// <summary>Fender Deluxe Reverb (blackface): smaller 1x12, breaks up earlier and sags more than the Twin.</summary>
public sealed class FenderDeluxeReverb : TubeAmp
{
    public FenderDeluxeReverb() : base("Fender Deluxe Reverb", new AmpVoicing
    {
        GainName = "Volume", HasMid = false, HasBright = true, HasReverb = true,
        InputHpHz = 30f, PreLpHz = 11000f,
        StageADb = 8f, KnobMinDb = -38f, KnobMaxDb = 14f, ExtraStages = 0, Bias = 0.15f,
        StackLowHz = 90f, StackHighHz = 2600f, BandRangeDb = 24f, MidBaseDb = -6f, RecoveryDb = 11f,
        BrightHz = 2000f,
        PowerMaxDb = 12f, Sag = 0.18f, TransformerHpHz = 45f, TransformerLpHz = 9500f, FixedPresence = 0.15f,
        CabHpHz = 95f, CabLpHz = 5000f, CabPeakHz = 2800f, CabPeakQ = 1.1f, CabPeakGain = 0.5f,
        OutTrimDb = 3f, ReverbFeedback = 0.7f, ReverbDamp = 0.5f,
    }) { }
}

/// <summary>Fender '57 Twin-Amp (5E8-A tweed Twin): loose, compressed, fuzzy breakup. No reverb, no mid control.</summary>
public sealed class Fender57TwinAmp : TubeAmp
{
    public Fender57TwinAmp() : base("Fender '57 Twin-Amp", new AmpVoicing
    {
        GainName = "Volume", TrebleName = "Tone", HasMid = false, HasBright = true,
        InputHpHz = 40f, PreLpHz = 6500f,
        StageADb = 6f, KnobMinDb = -30f, KnobMaxDb = 22f, ExtraStages = 0, Bias = 0.25f, CouplingHz = 70f,
        StackLowHz = 100f, StackHighHz = 2200f, BandRangeDb = 20f, MidBaseDb = -2f, RecoveryDb = 8f,
        BrightHz = 1500f,
        PowerMaxDb = 9f, Sag = 0.35f, TransformerHpHz = 55f, TransformerLpHz = 7000f, FixedPresence = 0.05f,
        CabHpHz = 80f, CabLpHz = 4200f, CabPeakHz = 2000f, CabPeakQ = 1.0f, CabPeakGain = 0.5f,
        OutTrimDb = -2f,
    }) { }
}

/// <summary>Mesa/Boogie Mark-style lead channel: tight, thick, multi-stage gain with a Contour mid scoop.</summary>
public sealed class MesaBoogie : TubeAmp
{
    public MesaBoogie() : base("Mesa/Boogie", new AmpVoicing
    {
        GainName = "Gain", HasPresence = true, HasMaster = true, HasContour = true,
        DefaultGain = 6f, DefaultMaster = 6f,
        InputHpHz = 140f, PreLpHz = 8000f,
        StageADb = 12f, KnobMinDb = -24f, KnobMaxDb = 12f, ExtraStages = 2, StageBDb = 10f, StageCDb = 8f, Bias = 0.2f,
        CouplingHz = 90f,
        StackLowHz = 120f, StackHighHz = 2400f, BandRangeDb = 22f, MidBaseDb = 0f, ContourMaxDb = 14f, RecoveryDb = 8f,
        PowerMaxDb = 10f, Sag = 0.3f, TransformerHpHz = 45f, TransformerLpHz = 9500f,
        PresenceHz = 3200f, PresenceMax = 0.9f,
        CabHpHz = 70f, CabLpHz = 5200f, CabPeakHz = 2600f, CabPeakQ = 1.3f, CabPeakGain = 0.6f,
        OutTrimDb = -3.5f,
    }) { }
}
