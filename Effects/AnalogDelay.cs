using System;

namespace Jrig.Effects;

/// <summary>
/// Bucket-brigade style analog delay: dark repeats (lowpass in the loop), saturation in the feedback path,
/// slow clock wobble, and a time control that glides like a real BBD when turned.
/// </summary>
public sealed class AnalogDelay : EffectBase
{

    const float ModRateHz = 0.6f;

    readonly EffectParameter time, feedback, tone, mod, mix;
    readonly DelayLine line = new();
    readonly Biquad loopLp = new();
    Smooth timeS;
    Lfo lfo;
    float sr = 48000f, lastTone = -1f;

    public AnalogDelay() : base("Analog Delay")
    {
        time = Add("time", "Time", 20f, 800f, 350f, " ms", ParameterCurve.Logarithmic, 0);
        feedback = Add("fb", "Feedback", 0f, 1.05f, 0.4f, "", ParameterCurve.Linear, 2);   // above 1.0 self-oscillates (bounded by the saturator)
        tone = Add("tone", "Tone", 0f, 1f, 0.4f, "", ParameterCurve.Linear, 2);
        mod = Add("mod", "Mod", 0f, 1f, 0.3f, "", ParameterCurve.Linear, 2);
        mix = Add("mix", "Mix", 0f, 100f, 35f, " %", ParameterCurve.Linear, 0);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        line.Allocate((int)(1.0f * sr));
        line.Clear();
        loopLp.Reset();
        timeS.Init(time.Value * 0.001f * sr, 60f, sr);   // slow glide gives the tape/BBD pitch swoop
        lfo.Reset();
        lastTone = -1f;
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float t = tone.Value;
        if (t != lastTone) { loopLp.Lowpass(Dsp.ExpMap(t, 900f, 5500f), 0.6f, sr); lastTone = t; }
        float tT = time.Value * 0.001f * sr;
        float fb = feedback.Value, modSamples = mod.Value * 0.0015f * sr, m = mix.Value * 0.01f;
        float maxD = line.Capacity - 8f;

        for (int i = 0; i < n; i++)
        {
            float d = timeS.Next(tT) + modSamples * Lfo.Sine(lfo.Advance(ModRateHz, sr));
            d = Dsp.Clamp(d, 2f, maxD);
            float x = buf[offset + i];
            float delayed = line.ReadHermite(d);
            float wet = loopLp.Process(delayed);
            line.Write(x + fb * Dsp.Tanh(wet * 0.9f) * 1.1f);   // saturating feedback keeps self-oscillation bounded
            buf[offset + i] = x + wet * m;
        }
    }
}
