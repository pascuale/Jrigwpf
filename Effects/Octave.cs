using System;

namespace Jrig.Effects;

/// <summary>
/// Analog-style octave pedal. Octave down uses a flip-flop that toggles on every cycle of the
/// low-passed input (square wave at f/2) shaped by the input envelope. Octave up is a full-wave rectifier.
/// Both are monophonic: play single notes, and expect tracking to break down on chords.
/// </summary>
public sealed class Octave : EffectBase
{

    readonly EffectParameter down, up, dry, tone;
    readonly Biquad detLp = new(), subLp = new(), upHp = new(), wetLp = new();
    EnvFollower envIn, envDet;
    Smooth downS, upS, dryS;
    float sr = 48000f, lastTone = -1f;
    bool flip, armed;

    public Octave() : base("Octave")
    {
        down = Add("down", "Oct Down", 0f, 1f, 0.6f, "", ParameterCurve.Linear, 2);
        up = Add("up", "Oct Up", 0f, 1f, 0.3f, "", ParameterCurve.Linear, 2);
        dry = Add("dry", "Dry", 0f, 1f, 0.8f, "", ParameterCurve.Linear, 2);
        tone = Add("tone", "Tone", 0f, 1f, 0.5f, "", ParameterCurve.Linear, 2);
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        detLp.Lowpass(380f, 0.707f, sr);
        subLp.Lowpass(700f, 0.707f, sr);
        upHp.Highpass(110f, 0.707f, sr);
        detLp.Reset(); subLp.Reset(); upHp.Reset(); wetLp.Reset();
        envIn.Set(3f, 60f, sr); envDet.Set(5f, 80f, sr);
        envIn.Reset(); envDet.Reset();
        downS.Init(down.Value, 10f, sr); upS.Init(up.Value, 10f, sr); dryS.Init(dry.Value, 10f, sr);
        flip = false; armed = false; lastTone = -1f;
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        float t = tone.Value;
        if (t != lastTone) { wetLp.Lowpass(Dsp.ExpMap(t, 600f, 5000f), 0.707f, sr); lastTone = t; }
        float dT = down.Value, uT = up.Value, yT = dry.Value;

        for (int i = 0; i < n; i++)
        {
            float x = buf[offset + i];

            // Octave down: Schmitt-triggered toggle on positive-going crossings of the low-passed signal.
            float lo = detLp.Process(x);
            float thr = Math.Max(0.002f, 0.2f * envDet.Next(lo));
            if (lo < -thr) armed = true;
            else if (armed && lo > thr) { flip = !flip; armed = false; }
            float sub = subLp.Process(flip ? envIn.Next(x) : -envIn.Next(x));

            // Octave up: rectify, then strip the DC it creates.
            float upSig = upHp.Process(MathF.Abs(x)) * 2f;

            float wet = sub * downS.Next(dT) * 1.4f + upSig * upS.Next(uT);
            buf[offset + i] = x * dryS.Next(yT) + wetLp.Process(wet);
        }
    }
}
