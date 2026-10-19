using System;

namespace Jrig.Effects;

/// <summary>
/// Wah-wah. "Position" is the heel-to-toe pedal (map a MIDI expression pedal or mouse to it later).
/// "Auto" runs an LFO instead for a hands-free auto-wah; 0 leaves the pedal in manual mode.
/// </summary>
public sealed class WahWah : EffectBase
{

    const float FLow = 350f, FHigh = 2200f;

    readonly EffectParameter position, q, auto, mix, level;
    readonly Svf svf = new();
    Smooth posS;
    Lfo lfo;
    float sr = 48000f;

    public WahWah() : base("Wah Wah")
    {
        position = Add("pos", "Position", 0f, 1f, 0.5f, "", ParameterCurve.Linear, 2);
        q = Add("q", "Q", 2f, 12f, 5f, "", ParameterCurve.Logarithmic, 1);
        auto = Add("auto", "Auto", 0f, 5f, 0f, " Hz", ParameterCurve.Linear, 1);
        mix = Add("mix", "Mix", 0f, 100f, 100f, " %", ParameterCurve.Linear, 0);
        level = Add("level", "Level", -12f, 12f, 0f, " dB", ParameterCurve.Linear, 1);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        svf.Reset();
        posS.Init(position.Value, 15f, sr);
        lfo.Reset();
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float rate = auto.Value, manual = position.Value;
        bool useLfo = rate > 0.05f;
        float qv = q.Value, m = mix.Value * 0.01f, lv = Dsp.DbToLin(level.Value);
        float ratio = FHigh / FLow;

        for (int i = 0; i < n; i++)
        {
            float target = useLfo ? 0.5f + 0.5f * Lfo.Sine(lfo.Advance(rate, sr)) : manual;
            float p = posS.Next(target);
            svf.Set(FLow * MathF.Pow(ratio, p), qv, sr);
            float x = buf[offset + i];
            svf.Tick(x);
            float wet = svf.Bp * svf.K * 2f;        // band-pass with makeup (the wah is naturally quiet)
            buf[offset + i] = (x + (wet - x) * m) * lv;
        }
    }
}
