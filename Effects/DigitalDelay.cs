using System;

namespace Jrig.Effects;

/// <summary>
/// Clean digital delay up to 2 s with high-cut and low-cut filters on the repeats.
/// </summary>
public sealed class DigitalDelay : EffectBase
{

    readonly EffectParameter time, feedback, hiCut, loCut, mix;
    readonly DelayLine line = new();
    readonly Biquad lp = new(), hp = new();
    Smooth timeS;
    float sr = 48000f, lastHi = -1f, lastLo = -1f;

    public DigitalDelay() : base("Digital Delay")
    {
        time = Add("time", "Time", 1f, 2000f, 400f, " ms", ParameterCurve.Logarithmic, 0);
        feedback = Add("fb", "Feedback", 0f, 0.95f, 0.35f, "", ParameterCurve.Linear, 2);
        hiCut = Add("hicut", "Hi Cut", 1000f, 16000f, 8000f, " Hz", ParameterCurve.Logarithmic, 0);
        loCut = Add("locut", "Lo Cut", 20f, 800f, 60f, " Hz", ParameterCurve.Logarithmic, 0);
        mix = Add("mix", "Mix", 0f, 100f, 30f, " %", ParameterCurve.Linear, 0);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        line.Allocate((int)(2.0f * sr));
        line.Clear();
        lp.Reset(); hp.Reset();
        timeS.Init(time.Value * 0.001f * sr, 25f, sr);
        lastHi = -1f; lastLo = -1f;
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float hi = hiCut.Value, lo = loCut.Value;
        if (hi != lastHi) { lp.Lowpass(hi, 0.707f, sr); lastHi = hi; }
        if (lo != lastLo) { hp.Highpass(lo, 0.707f, sr); lastLo = lo; }
        float tT = time.Value * 0.001f * sr, fb = feedback.Value, m = mix.Value * 0.01f;
        float maxD = line.Capacity - 8f;

        for (int i = 0; i < n; i++)
        {
            float d = Dsp.Clamp(timeS.Next(tT), 2f, maxD);
            float x = buf[offset + i];
            float delayed = line.ReadHermite(d);
            float wet = hp.Process(lp.Process(delayed));
            line.Write(x + Dsp.SoftLimit(wet * fb));
            buf[offset + i] = x + wet * m;
        }
    }
}
