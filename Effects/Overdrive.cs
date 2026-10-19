using System;

namespace Jrig.Effects;

/// <summary>
/// Tube-screamer style overdrive: mid-focused pre-filter, asymmetric soft clip, tone lowpass, level.
/// </summary>
public sealed class Overdrive : EffectBase
{

    readonly EffectParameter drive, tone, level;
    readonly Biquad pre = new(), post = new(), dc = new();
    Smooth driveS, levelS;
    float sr = 48000f, lastTone = -1f;

    public Overdrive() : base("Overdrive")
    {
        drive = Add("drive", "Drive", 0f, 36f, 14f, " dB", ParameterCurve.Linear, 1);
        tone = Add("tone", "Tone", 0f, 1f, 0.5f, "", ParameterCurve.Linear, 2);
        level = Add("level", "Level", -24f, 12f, -3f, " dB", ParameterCurve.Linear, 1);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        pre.Highpass(300f, 0.707f, sr);
        dc.Highpass(20f, 0.707f, sr);
        pre.Reset(); post.Reset(); dc.Reset();
        driveS.Init(Dsp.DbToLin(drive.Value), 10f, sr);
        levelS.Init(Dsp.DbToLin(level.Value), 10f, sr);
        lastTone = -1f;
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float t = tone.Value;
        if (t != lastTone) { post.Lowpass(Dsp.ExpMap(t, 700f, 7000f), 0.707f, sr); lastTone = t; }
        float dT = Dsp.DbToLin(drive.Value), lT = Dsp.DbToLin(level.Value);

        for (int i = 0; i < n; i++)
        {
            float g = driveS.Next(dT), l = levelS.Next(lT);
            float x = buf[offset + i];
            // Boost the mids going into the clipper, keep some full-range signal for body.
            float v = (x + 0.6f * pre.Process(x)) * g;
            // Asymmetric soft clip: positive half clips a little harder than the negative half.
            float y = v >= 0f ? Dsp.Tanh(v) : 0.92f * Dsp.Tanh(v * 1.1f);
            y = dc.Process(y);              // asymmetry creates DC
            buf[offset + i] = post.Process(y) * l;
        }
    }
}
