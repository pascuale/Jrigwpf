using System;

namespace Jrig.Effects;

/// <summary>
/// Leslie rotating speaker cabinet (mono, one virtual microphone). The signal goes through a tube-style
/// preamp, is split at ~800 Hz into a treble horn and a bass drum rotor, and each rotor spins at its own speed.
/// A spinning source does three things to a fixed microphone, and all three are modelled:
///   - Doppler: the delay to the mic rises and falls, bending the pitch (the classic warble),
///   - amplitude: the sound is loudest when the rotor faces the mic,
///   - directionality: the horn's highs are a narrow beam, so it is brighter when it faces the mic.
/// Speed switches Stop / Chorale (slow) / Tremolo (fast). The rotors ramp between speeds with different inertia:
/// the light horn changes quickly, the heavy drum lags behind, which is a big part of the real sound.
/// </summary>
public sealed class Leslie : EffectBase
{
    static readonly string[] SpeedNames = { "Stop", "Chorale", "Tremolo" };

    // Rotor speeds in Hz (index = Speed knob): stopped, slow (chorale), fast (tremolo).
    static readonly float[] HornHz = { 0f, 0.8f, 6.7f };
    static readonly float[] DrumHz = { 0f, 0.67f, 5.9f };

    const float CrossoverHz = 800f;
    const float HornSpinMs = 700f;       // time constant of the horn ramping to a new speed
    const float DrumSpinMs = 3000f;      // the heavy drum takes much longer

    readonly EffectParameter speed, depth, drive, balance, level;
    readonly Biquad hpA = new(), hpB = new(), lpA = new(), lpB = new();
    readonly DelayLine hornLine = new(), drumLine = new();
    Lfo hornLfo, drumLfo;
    Smooth depthS, driveS, balS, levelS;
    float sr = 48000f;
    float hornRate, drumRate, hornCoef, drumCoef;
    float hornLp, hornLpCoef;

    public Leslie() : base("Leslie Rotary Speaker")
    {
        speed = Add("speed", "Speed", 0f, 2f, 1f, "", ParameterCurve.Linear, 0);
        speed.Labels = SpeedNames;
        depth = Add("depth", "Depth", 0f, 1f, 0.7f, "", ParameterCurve.Linear, 2);
        drive = Add("drive", "Drive", 0f, 1f, 0.25f, "", ParameterCurve.Linear, 2);
        balance = Add("balance", "Balance", 0f, 1f, 0.5f, "", ParameterCurve.Linear, 2);
        level = Add("level", "Level", 0f, 2f, 1f, "", ParameterCurve.Linear, 2);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        // 4th-order Linkwitz-Riley style split: two cascaded 2nd-order Butterworth sections per band.
        hpA.Highpass(CrossoverHz, 0.707f, sr); hpB.Highpass(CrossoverHz, 0.707f, sr);
        lpA.Lowpass(CrossoverHz, 0.707f, sr); lpB.Lowpass(CrossoverHz, 0.707f, sr);
        hpA.Reset(); hpB.Reset(); lpA.Reset(); lpB.Reset();

        int maxDelay = (int)(0.004f * sr);
        hornLine.Allocate(maxDelay); hornLine.Clear();
        drumLine.Allocate(maxDelay); drumLine.Clear();

        hornLfo.Reset(0f);
        drumLfo.Reset(0.3f);              // rotors do not start in step with each other

        int s = SpeedIndex();
        hornRate = HornHz[s]; drumRate = DrumHz[s];
        hornCoef = 1f - MathF.Exp(-1f / (0.001f * HornSpinMs * sr));
        drumCoef = 1f - MathF.Exp(-1f / (0.001f * DrumSpinMs * sr));

        hornLp = 0f;
        hornLpCoef = 1f - MathF.Exp(-Dsp.TwoPi * 2800f / sr);

        depthS.Init(depth.Value, 20f, sr);
        driveS.Init(drive.Value, 20f, sr);
        balS.Init(balance.Value, 20f, sr);
        levelS.Init(level.Value, 20f, sr);
    }

    int SpeedIndex() => Math.Clamp((int)MathF.Round(speed.Value), 0, 2);

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        int si = SpeedIndex();
        float hornTarget = HornHz[si], drumTarget = DrumHz[si];
        float dT = depth.Value, gT = drive.Value, bT = balance.Value, lT = level.Value;

        float baseSamples = 0.0012f * sr;       // resting delay to the mic
        float swingMax = 0.0007f * sr;          // horn: +-0.7 ms at full depth (about +-2% pitch at 6.7 Hz)

        for (int i = 0; i < n; i++)
        {
            // Rotors spin up and down towards the chosen speed.
            hornRate += (hornTarget - hornRate) * hornCoef;
            drumRate += (drumTarget - drumRate) * drumCoef;
            float hp = hornLfo.Advance(hornRate, sr);
            float dp = drumLfo.Advance(drumRate, sr);
            float hc = Lfo.Sine(hp + 0.25f);    // cos(angle): 1 = facing the mic
            float dc = Lfo.Sine(dp + 0.25f);

            float dep = depthS.Next(dT);
            float drv = driveS.Next(gT);
            float bal = balS.Next(bT);

            // Tube preamp: unity at low levels, rounds off peaks as Drive rises.
            float x = buf[offset + i];
            float g = 1f + 6f * drv;
            float pre = Dsp.Tanh(x * g) / g * (1f + 1.5f * drv);

            // Crossover.
            float hi = hpB.Process(hpA.Process(pre));
            float lo = lpB.Process(lpA.Process(pre));

            // Horn: Doppler, directional brightness and loudness.
            hornLine.Write(hi);
            float hd = baseSamples - dep * swingMax * hc;
            float h = hornLine.ReadHermite(hd);
            hornLp = Dsp.Flush(hornLp + hornLpCoef * (h - hornLp));
            float hBeam = 0.5f + 0.5f * hc;                         // 1 facing the mic, 0 facing away
            float hAway = dep * (1f - hBeam);
            h = hornLp + (h - hornLp) * (1f - 0.65f * hAway);       // lose the highs turned away
            h *= 1f - 0.6f * hAway;                                 // and some level

            // Drum: slower, mostly amplitude, a little Doppler.
            drumLine.Write(lo);
            float dd = baseSamples - dep * swingMax * 0.5f * dc;
            float d = drumLine.ReadHermite(dd);
            float dBeam = 0.5f + 0.5f * dc;
            d *= 1f - 0.35f * dep * (1f - dBeam);

            float wet = h * 2f * bal + d * 2f * (1f - bal);
            buf[offset + i] = Dsp.SoftLimit(wet * levelS.Next(lT));
        }
    }
}
