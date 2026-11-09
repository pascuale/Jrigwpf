using System;

namespace Jrig.Effects;

/// <summary>
/// Pitch shifter: two delay-line taps whose read position slides at (1 - ratio) samples per sample, so the
/// tape-style playback speed equals the pitch ratio. The taps are half a window apart and crossfaded with
/// a sin^2 / cos^2 window, so as one tap wraps around (and jumps) it is faded out while the other is at full level.
/// Works on chords as well as single notes, with no pitch tracking. The price is latency of about half the
/// window, and a slight warble, mostly on low notes and big shifts. A longer Window is smoother on low
/// notes and a shorter one has less delay.
/// </summary>
public sealed class PitchShifter : EffectBase
{
    readonly EffectParameter shift, fine, window, tone, mix;
    readonly DelayLine line = new();
    readonly Biquad wetLp = new();
    Smooth shiftS, winS, mixS;
    float sr = 48000f, phase, lastTone = -1f;

    public PitchShifter() : base("Pitch Shifter")
    {
        shift = Add("shift", "Shift", -24f, 24f, 12f, " st", ParameterCurve.Linear, 0);
        fine = Add("fine", "Fine", -50f, 50f, 0f, " ct", ParameterCurve.Linear, 0);
        window = Add("window", "Window", 20f, 100f, 50f, " ms", ParameterCurve.Linear, 0);
        tone = Add("tone", "Tone", 0f, 1f, 0.7f, "", ParameterCurve.Linear, 2);
        mix = Add("mix", "Mix", 0f, 100f, 50f, " %", ParameterCurve.Linear, 0);
    }

    float TotalSemitones() => MathF.Round(shift.Value) + fine.Value * 0.01f;

    protected override void OnPrepare()
    {
        sr = SampleRate;
        line.Allocate((int)(0.11f * sr) + 8);
        line.Clear();
        phase = 0f;
        wetLp.Reset();
        lastTone = -1f;
        shiftS.Init(TotalSemitones(), 15f, sr);
        winS.Init(window.Value, 150f, sr);       // slow, so moving Window does not click
        mixS.Init(mix.Value * 0.01f, 20f, sr);
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float t = tone.Value;
        if (t != lastTone) { wetLp.Lowpass(Dsp.ExpMap(t, 2000f, 12000f), 0.707f, sr); lastTone = t; }
        float sT = TotalSemitones(), wT = window.Value, mT = mix.Value * 0.01f;

        for (int i = 0; i < n; i++)
        {
            float w = winS.Next(wT) * 0.001f * sr;               // window length in samples
            float ratio = MathF.Pow(2f, shiftS.Next(sT) / 12f);

            // Slide the read position: delay = base + phase * window, shrinking when pitching up.
            phase += (1f - ratio) / w;
            if (phase >= 1f) phase -= 1f;
            else if (phase < 0f) phase += 1f;
            float p2 = phase + 0.5f;
            if (p2 >= 1f) p2 -= 1f;

            float v1 = line.ReadHermite(3f + phase * w);
            float v2 = line.ReadHermite(3f + p2 * w);
            float x = buf[offset + i];
            line.Write(x);

            float s = MathF.Sin(MathF.PI * phase);
            float g1 = s * s, g2 = 1f - g1;                      // sin^2 + cos^2 = 1: constant level crossfade
            float wet = wetLp.Process(v1 * g1 + v2 * g2);

            float m = mixS.Next(mT);
            buf[offset + i] = x + (wet - x) * m;
        }
    }
}
