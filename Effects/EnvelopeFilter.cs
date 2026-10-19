using System;

namespace Jrig.Effects;

/// <summary>
/// Envelope filter (auto-wah): a band-pass filter whose cutoff follows the playing dynamics.
/// </summary>
public sealed class EnvelopeFilter : EffectBase
{

    readonly EffectParameter sens, minFreq, range, q, attack, release, dir, mix;
    readonly Svf svf = new();
    EnvFollower env;
    Smooth cutS;
    float sr = 48000f;

    public EnvelopeFilter() : base("Envelope Filter")
    {
        sens = Add("sens", "Sens", 0f, 1f, 0.6f, "", ParameterCurve.Linear, 2);
        minFreq = Add("min", "Min Freq", 80f, 800f, 200f, " Hz", ParameterCurve.Logarithmic, 0);
        range = Add("range", "Range", 1f, 6f, 3.5f, " oct", ParameterCurve.Linear, 1);
        q = Add("q", "Q", 0.7f, 10f, 3f, "", ParameterCurve.Logarithmic, 1);
        attack = Add("attack", "Attack", 1f, 50f, 8f, " ms", ParameterCurve.Logarithmic, 1);
        release = Add("release", "Release", 20f, 500f, 120f, " ms", ParameterCurve.Logarithmic, 0);
        dir = Add("dir", "Down", 0f, 1f, 0f, "", ParameterCurve.Linear, 0);          // < 0.5 sweeps up, >= 0.5 sweeps down
        mix = Add("mix", "Mix", 0f, 100f, 100f, " %", ParameterCurve.Linear, 0);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        env.Reset(); svf.Reset();
        cutS.Init(0f, 5f, sr);
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        env.Set(attack.Value, release.Value, sr);
        float fMin = minFreq.Value;
        float fMax = Math.Min(fMin * MathF.Pow(2f, range.Value), sr * 0.4f);
        float ratio = fMax / fMin;
        float scale = 2f + 58f * sens.Value;            // envelope gain, 2..60
        bool down = dir.Value >= 0.5f;
        float qv = q.Value, m = mix.Value * 0.01f;

        for (int i = 0; i < n; i++)
        {
            float x = buf[offset + i];
            float e = Dsp.Clamp(env.Next(x) * scale, 0f, 1f);
            e = MathF.Sqrt(e);                           // gentler response at low levels
            float pos = cutS.Next(down ? 1f - e : e);
            float fc = fMin * MathF.Pow(ratio, pos);
            svf.Set(fc, qv, sr);
            svf.Tick(x);
            float wet = svf.Bp * svf.K * 1.4f;           // unity peak band-pass plus a little makeup
            buf[offset + i] = x + (wet - x) * m;
        }
    }
}
