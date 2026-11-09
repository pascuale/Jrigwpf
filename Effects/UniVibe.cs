using System;

namespace Jrig.Effects;

/// <summary>
/// Uni-Vibe style phase shifter with a Chorus / Vibrato switch.
///
/// The original uses four all-pass stages whose corner frequencies are set by photocells (LDRs) lit by
/// one incandescent lamp, and the lamp is driven by a slow oscillator. Three things give it its sound:
///   * the four stages have different corner ranges (different capacitor values), so the notches
///     are staggered instead of stacked like a phaser,
///   * the lamp is slow to heat and cool, so the sweep is lopsided and loses width at high speeds,
///   * the photocells respond exponentially, so the sweep is even across the octaves.
/// Chorus mode mixes the phase-shifted signal 50/50 with the dry signal (moving notches).
/// Vibrato mode plays the phase-shifted signal only (the moving phase becomes a pitch wobble).
/// </summary>
public sealed class UniVibe : EffectBase
{
    const int Stages = 4;

    /// <summary>Lowest corner frequency of each stage (dark lamp), Hz. Staggered like the original's capacitors.</summary>
    static readonly float[] LowHz = { 700f, 320f, 160f, 80f };

    /// <summary>Each stage sweeps 16x (4 octaves) from dark lamp to full brightness.</summary>
    static readonly float LnRatio = MathF.Log(16f);

    /// <summary>In Vibrato mode the lamp swing is reduced so full Intensity is a musical wobble (about +-40 cents), not a siren.</summary>
    const float VibratoSwing = 0.35f;

    static readonly string[] ModeNames = { "Chorus", "Vibrato" };

    readonly EffectParameter speed, intensity, mode, level;
    readonly float[] z = new float[Stages];
    Lfo lfo;
    Smooth modeS, levelS;
    float sr = 48000f, lamp = 0.5f, lampAtk, lampRel;

    public UniVibe() : base("Uni-Vibe")
    {
        speed = Add("speed", "Speed", 0.5f, 10f, 3.5f, " Hz", ParameterCurve.Logarithmic, 1);
        intensity = Add("intensity", "Intensity", 0f, 1f, 0.7f, "", ParameterCurve.Linear, 2);
        mode = Add("mode", "Mode", 0f, ModeNames.Length - 1, 0f, "", ParameterCurve.Linear, 0);
        mode.Labels = ModeNames;
        level = Add("level", "Volume", -12f, 12f, 0f, " dB", ParameterCurve.Linear, 1);
    }

    float VibratoTarget() => MathF.Round(mode.Value) >= 1f ? 1f : 0f;

    protected override void OnPrepare()
    {
        sr = SampleRate;
        Array.Clear(z, 0, z.Length);
        lfo.Reset();
        lamp = 0.5f;
        lampAtk = 1f - Dsp.TimeToCoef(8f, sr);     // lamp heats quickly...
        lampRel = 1f - Dsp.TimeToCoef(25f, sr);    // ...and cools slowly
        modeS.Init(VibratoTarget(), 30f, sr);
        levelS.Init(Dsp.DbToLin(level.Value), 10f, sr);
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float r = speed.Value, inten = intensity.Value;
        float vT = VibratoTarget();
        float lT = Dsp.DbToLin(level.Value);
        float fMax = sr * 0.45f;

        for (int i = 0; i < n; i++)
        {
            float ph = lfo.Advance(r, sr);
            float v = modeS.Next(vT);                                   // 0 = chorus, 1 = vibrato (smoothed, no click)
            float swing = inten * (1f + (VibratoSwing - 1f) * v);
            float drive = 0.5f + 0.5f * swing * Lfo.Sine(ph);           // lamp drive, 0..1

            lamp += (drive - lamp) * (drive > lamp ? lampAtk : lampRel); // lamp thermal lag

            float x = buf[offset + i];
            float y = x;
            for (int k = 0; k < Stages; k++)
            {
                float f = Dsp.Clamp(LowHz[k] * MathF.Exp(lamp * LnRatio), 20f, fMax);
                float t = MathF.Tan(MathF.PI * f / sr);
                float a = (t - 1f) / (t + 1f);
                float o = a * y + z[k];                                  // first-order all-pass, pole at the corner frequency
                z[k] = Dsp.Flush(y - a * o);
                y = o;
            }

            float m = 0.5f + 0.5f * v;                                   // wet amount: 50 % chorus, 100 % vibrato
            buf[offset + i] = (x + (y - x) * m) * levelS.Next(lT);
        }
    }
}
