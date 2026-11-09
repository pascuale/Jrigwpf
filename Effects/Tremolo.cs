using System;

namespace Jrig.Effects;

/// <summary>
/// Tremolo: a low-frequency wobble in volume. Shape picks the wave:
///   Sine, Triangle and Square (the square has softened edges, so it pulses without clicking), and
///   Harmonic, the brown-face amp style, where the lows and highs (split at 600 Hz) swell in opposite
///   directions for a rounder, phasey throb.
/// The wave only ever turns the volume down from your playing level, so Depth 1 = silence at the bottom
/// of each cycle and Depth 0 = no effect. Gains are smoothed over a few ms so changing Shape never clicks.
/// </summary>
public sealed class Tremolo : EffectBase
{
    static readonly string[] ShapeNames = { "Sine", "Triangle", "Square", "Harmonic" };

    const float SplitHz = 600f;

    readonly EffectParameter rate, depth, shape, level;
    readonly Biquad lp = new();
    Lfo lfo;
    Smooth depthS, loS, hiS, levelS;
    float sr = 48000f;

    public Tremolo() : base("Tremolo")
    {
        rate = Add("rate", "Rate", 0.5f, 15f, 4f, " Hz", ParameterCurve.Logarithmic, 1);
        depth = Add("depth", "Depth", 0f, 1f, 0.6f, "", ParameterCurve.Linear, 2);
        shape = Add("shape", "Shape", 0f, ShapeNames.Length - 1, 0f, "", ParameterCurve.Linear, 0);
        shape.Labels = ShapeNames;
        level = Add("level", "Level", -12f, 12f, 0f, " dB", ParameterCurve.Linear, 1);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        lp.Lowpass(SplitHz, 0.707f, sr);
        lp.Reset();
        lfo.Reset();
        depthS.Init(depth.Value, 20f, sr);
        loS.Init(1f, 3f, sr);
        hiS.Init(1f, 3f, sr);
        levelS.Init(Dsp.DbToLin(level.Value), 20f, sr);
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float r = rate.Value, dT = depth.Value, lT = Dsp.DbToLin(level.Value);
        int sh = Math.Clamp((int)MathF.Round(shape.Value), 0, ShapeNames.Length - 1);

        for (int i = 0; i < n; i++)
        {
            float p = lfo.Advance(r, sr);
            float d = depthS.Next(dT);

            // u: 0..1 wave, 1 = full volume, 0 = the bottom of the dip.
            float u;
            switch (sh)
            {
                case 1: u = p < 0.5f ? 2f * p : 2f - 2f * p; break;                   // triangle
                case 2: u = 0.5f + 0.5f * Dsp.Tanh(4f * Lfo.Sine(p)); break;          // soft square
                default: u = 0.5f + 0.5f * Lfo.Sine(p); break;                        // sine (and harmonic)
            }

            // Normal shapes: lows and highs share one gain. Harmonic: they move in opposition.
            float gLo = 1f - d * (1f - u);
            float gHi = sh == 3 ? 1f - d * u : gLo;
            gLo = loS.Next(gLo);
            gHi = hiS.Next(gHi);

            float x = buf[offset + i];
            float lo = lp.Process(x);
            float hi = x - lo;                      // complementary split: lo + hi == x exactly
            buf[offset + i] = (lo * gLo + hi * gHi) * levelS.Next(lT);
        }
    }
}
