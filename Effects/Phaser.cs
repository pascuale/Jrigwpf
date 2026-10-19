using System;

namespace Jrig.Effects;

/// <summary>
/// Phaser: up to 8 first-order all-pass stages swept by a sine LFO, with feedback.
/// </summary>
public sealed class Phaser : EffectBase
{

    const int MaxStages = 8;

    readonly EffectParameter rate, depth, center, feedback, stages, mix;
    readonly float[] z = new float[MaxStages];
    Lfo lfo;
    Smooth mixS;
    float sr = 48000f, last;

    public Phaser() : base("Phaser")
    {
        rate = Add("rate", "Rate", 0.05f, 5f, 0.4f, " Hz", ParameterCurve.Logarithmic, 2);
        depth = Add("depth", "Depth", 0f, 1f, 0.7f, "", ParameterCurve.Linear, 2);
        center = Add("center", "Center", 200f, 2000f, 600f, " Hz", ParameterCurve.Logarithmic, 0);
        feedback = Add("fb", "Feedback", -0.9f, 0.9f, 0.4f, "", ParameterCurve.Linear, 2);
        stages = Add("stages", "Stages", 2f, 8f, 4f, "", ParameterCurve.Linear, 0);
        mix = Add("mix", "Mix", 0f, 100f, 50f, " %", ParameterCurve.Linear, 0);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        Array.Clear(z, 0, z.Length);
        last = 0f;
        lfo.Reset();
        mixS.Init(mix.Value * 0.01f, 20f, sr);
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float r = rate.Value, dp = depth.Value, c = center.Value, fb = feedback.Value, mT = mix.Value * 0.01f;
        int st = (int)MathF.Round(Dsp.Clamp(stages.Value, 2f, MaxStages));
        float fMax = sr * 0.45f;

        for (int i = 0; i < n; i++)
        {
            float s = Lfo.Sine(lfo.Advance(r, sr));
            float f = Dsp.Clamp(c * MathF.Pow(2f, dp * 2f * s), 20f, fMax);   // sweep +-2 octaves at full depth
            float t = MathF.Tan(MathF.PI * f / sr);
            float a = (t - 1f) / (t + 1f);

            float x = buf[offset + i];
            float y = x + fb * last;
            for (int k = 0; k < st; k++)
            {
                float o = -a * y + z[k];
                z[k] = Dsp.Flush(y + a * o);
                y = o;
            }
            last = Dsp.SoftLimit(y);
            float m = mixS.Next(mT);
            buf[offset + i] = x + (y - x) * m;
        }
    }
}
