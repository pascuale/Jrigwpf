using System;

namespace Jrig.Effects;

/// <summary>
/// Mu-Tron style Octave Divider. Like the original it is a pure analog "divider": the input is low-passed and
/// squared up by a Schmitt trigger, then a chain of two flip-flops divides the frequency by 2 (one octave down)
/// and by 4 (two octaves down). Each square wave is gated by the picking envelope (the original uses an envelope
/// follower driving a VCA), so the sub tones swell and decay with your string, then smoothed by a low-pass.
/// Monophonic by nature: play single notes, ideally on the lower strings and neck. Chords and very soft notes
/// make the dividers glitch, which is part of the sound.
/// </summary>
public sealed class MutronOctaveDivider : EffectBase
{
    readonly EffectParameter dry, oct1, oct2, sense, tone;
    readonly Biquad detLp = new(), sub1Lp = new(), sub2Lp = new(), wetLp = new();
    EnvFollower envIn, envDet;
    Smooth dryS, oct1S, oct2S;
    float sr = 48000f, lastTone = -1f;
    bool armed, ff1, ff2;

    public MutronOctaveDivider() : base("Mu-Tron Octave Divider")
    {
        dry = Add("dry", "Dry", 0f, 1f, 0.7f, "", ParameterCurve.Linear, 2);
        oct1 = Add("oct1", "Oct -1", 0f, 1f, 0.7f, "", ParameterCurve.Linear, 2);
        oct2 = Add("oct2", "Oct -2", 0f, 1f, 0.3f, "", ParameterCurve.Linear, 2);
        sense = Add("sense", "Sense", 0f, 1f, 0.5f, "", ParameterCurve.Linear, 2);
        tone = Add("tone", "Tone", 0f, 1f, 0.4f, "", ParameterCurve.Linear, 2);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        detLp.Lowpass(380f, 0.707f, sr);      // keeps the trigger on the fundamental
        sub1Lp.Lowpass(750f, 0.707f, sr);     // rounds off the f/2 square
        sub2Lp.Lowpass(350f, 0.707f, sr);     // rounds off the f/4 square
        detLp.Reset(); sub1Lp.Reset(); sub2Lp.Reset(); wetLp.Reset();
        envIn.Set(3f, 70f, sr); envDet.Set(5f, 80f, sr);
        envIn.Reset(); envDet.Reset();
        dryS.Init(dry.Value, 10f, sr); oct1S.Init(oct1.Value, 10f, sr); oct2S.Init(oct2.Value, 10f, sr);
        armed = false; ff1 = false; ff2 = false; lastTone = -1f;
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float t = tone.Value;
        if (t != lastTone) { wetLp.Lowpass(Dsp.ExpMap(t, 500f, 4500f), 0.707f, sr); lastTone = t; }

        float s = sense.Value;
        float floor = Dsp.ExpMap(1f - s, 0.0006f, 0.02f);   // higher Sense = quieter notes still trigger
        float frac = 0.28f - 0.16f * s;                      // Schmitt hysteresis relative to the signal level
        float dT = dry.Value, aT = oct1.Value, bT = oct2.Value;

        for (int i = 0; i < n; i++)
        {
            float x = buf[offset + i];

            // Schmitt trigger on the low-passed input; each full cycle toggles the first flip-flop.
            float lo = detLp.Process(x);
            float thr = Math.Max(floor, frac * envDet.Next(lo));
            if (lo < -thr) armed = true;
            else if (armed && lo > thr)
            {
                armed = false;
                ff1 = !ff1;
                if (ff1) ff2 = !ff2;                          // second divider is clocked by the first
            }

            // Envelope gate: the squares only sound while a note is ringing, and follow its loudness.
            float env = envIn.Next(x);
            float gate = env < floor ? 0f : Math.Min(env * 1.6f, 1f);

            float a = sub1Lp.Process(ff1 ? gate : -gate);
            float b = sub2Lp.Process(ff2 ? gate : -gate);

            float wet = a * oct1S.Next(aT) * 1.1f + b * oct2S.Next(bT) * 1.3f;
            buf[offset + i] = x * dryS.Next(dT) + wetLp.Process(wet);
        }
    }
}
