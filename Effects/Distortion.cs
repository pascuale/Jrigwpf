using System;

namespace Jrig.Effects;

/// <summary>
/// High-gain distortion: tight pre-filter, very high gain, soft-to-hard clipper blend ("Voice"), tone lowpass.
/// </summary>
public sealed class Distortion : EffectBase
{

    readonly EffectParameter gain, voice, tone, level;
    readonly Biquad pre = new(), post = new(), dc = new();
    Smooth gainS, levelS;
    float sr = 48000f, lastTone = -1f;

    public Distortion() : base("Distortion")
    {
        gain = Add("gain", "Gain", 10f, 60f, 35f, " dB", ParameterCurve.Linear, 1);
        voice = Add("voice", "Voice", 0f, 1f, 0.5f, "", ParameterCurve.Linear, 2);   // 0 = soft, 1 = hard clip
        tone = Add("tone", "Tone", 0f, 1f, 0.5f, "", ParameterCurve.Linear, 2);
        level = Add("level", "Level", -30f, 6f, -12f, " dB", ParameterCurve.Linear, 1);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        pre.Highpass(110f, 0.707f, sr);     // keeps low-end flub out of the clipper
        dc.Highpass(20f, 0.707f, sr);
        pre.Reset(); post.Reset(); dc.Reset();
        gainS.Init(Dsp.DbToLin(gain.Value), 10f, sr);
        levelS.Init(Dsp.DbToLin(level.Value), 10f, sr);
        lastTone = -1f;
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float t = tone.Value, vo = voice.Value;
        if (t != lastTone) { post.Lowpass(Dsp.ExpMap(t, 1500f, 9000f), 0.707f, sr); lastTone = t; }
        float gT = Dsp.DbToLin(gain.Value), lT = Dsp.DbToLin(level.Value);

        for (int i = 0; i < n; i++)
        {
            float g = gainS.Next(gT), l = levelS.Next(lT);
            float v = pre.Process(buf[offset + i]) * g;
            float soft = Dsp.Tanh(v);
            float hard = Dsp.Clamp(v, -1f, 1f);
            float y = soft + (hard - soft) * vo;
            // Slight asymmetry adds even harmonics, then remove the resulting DC.
            y = dc.Process(y + 0.05f * y * y);
            buf[offset + i] = post.Process(y) * l;
        }
    }
}
