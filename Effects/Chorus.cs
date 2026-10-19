using System;

namespace Jrig.Effects;

/// <summary>
/// Two-voice chorus: two short modulated delay taps driven by opposite-phase sine LFOs.
/// </summary>
public sealed class Chorus : EffectBase
{

    readonly EffectParameter rate, depth, delay, mix;
    readonly DelayLine line = new();
    Lfo lfo;
    Smooth depthS, mixS;
    float sr = 48000f;

    public Chorus() : base("Chorus")
    {
        rate = Add("rate", "Rate", 0.1f, 8f, 0.8f, " Hz", ParameterCurve.Logarithmic, 2);
        depth = Add("depth", "Depth", 0f, 1f, 0.5f, "", ParameterCurve.Linear, 2);
        delay = Add("delay", "Delay", 5f, 15f, 8f, " ms", ParameterCurve.Linear, 1);
        mix = Add("mix", "Mix", 0f, 100f, 50f, " %", ParameterCurve.Linear, 0);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        line.Allocate((int)(0.04f * sr));
        line.Clear();
        lfo.Reset();
        depthS.Init(depth.Value, 20f, sr);
        mixS.Init(mix.Value * 0.01f, 20f, sr);
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float r = rate.Value;
        float baseSamples = delay.Value * 0.001f * sr;
        float dT = depth.Value, mT = mix.Value * 0.01f;

        for (int i = 0; i < n; i++)
        {
            float ph = lfo.Advance(r, sr);
            float d = depthS.Next(dT) * baseSamples * 0.5f;     // max swing +-50% of base delay, so min delay stays > 2 samples
            float s1 = Lfo.Sine(ph), s2 = -s1;
            float v1 = line.ReadHermite(baseSamples + d * s1);
            float v2 = line.ReadHermite(baseSamples + d * s2);
            float x = buf[offset + i];
            line.Write(x);
            float wet = 0.5f * (v1 + v2);
            float m = mixS.Next(mT);
            buf[offset + i] = x + (wet - x) * m;
        }
    }
}
