using System;

namespace Jrig.Effects;

/// <summary>
/// Low-latency monophonic pitch tracker (YIN on a ~11-12 kHz decimated copy, analysed every ~13 ms).
/// Feed it every input sample with Process(). It reports Frequency while Locked.
/// The integration window is anchored on the newest samples, so the estimate lags the string by about one
/// analysis window. A jump to a new pitch is believed only after two analyses in a row agree, which rejects
/// one-frame octave errors. Call Unlock() at a new note onset so the first lock is accepted immediately.
/// All buffers are allocated up front; Process() is allocation-free (audio thread).
/// </summary>
internal sealed class PitchTracker
{
    const int Window = 640;            // decimated samples kept
    const int Hop = 160;               // analysis every ~13 ms
    const int Half = 400;              // integration length (newest samples)
    const int MaxLag = 220;            // lowest detectable ~ 50 Hz
    const int MinLag = 6;              // highest detectable ~ 1.9 kHz
    const float YinThreshold = 0.15f;
    const float SilenceRms = 0.003f;
    const int MaxFails = 12;           // ~160 ms of failed analyses before the pitch is considered lost

    readonly Biquad antiAlias = new();
    readonly float[] frame = new float[Window];
    readonly float[] diff = new float[MaxLag + 1];
    readonly float[] cmnd = new float[MaxLag + 1];
    int decim = 4, decimCount, fill, fails;
    float fs = 12000f, sr = 48000f, pendingFreq;

    /// <summary>Tracked pitch in Hz. Only meaningful while <see cref="Locked"/>.</summary>
    public float Frequency { get; private set; }
    public bool Locked { get; private set; }

    public void Prepare(float sampleRate)
    {
        sr = sampleRate;
        decim = Math.Max(1, (int)MathF.Round(sr / 12000f));
        fs = sr / decim;
        antiAlias.Lowpass(MathF.Min(2500f, fs * 0.4f), 0.707f, sr);
        antiAlias.Reset();
        Array.Clear(frame, 0, frame.Length);
        decimCount = 0; fill = 0; fails = 0; pendingFreq = 0f;
        Frequency = 0f; Locked = false;
    }

    /// <summary>Forget the current pitch so the next detection is accepted at once (new note).</summary>
    public void Unlock()
    {
        Locked = false; fails = 0; pendingFreq = 0f;
    }

    public void Process(float x)
    {
        float lp = antiAlias.Process(x);
        if (++decimCount < decim) return;
        decimCount = 0;
        frame[fill++] = lp;
        if (fill >= Window)
        {
            Analyze();
            Array.Copy(frame, Hop, frame, 0, Window - Hop);
            fill = Window - Hop;
        }
    }

    void Analyze()
    {
        int b = Window - 1;

        double en = 0;
        for (int j = 0; j < Half; j++) { float s = frame[b - j]; en += s * s; }
        if ((float)Math.Sqrt(en / Half) < SilenceRms) { Fail(); return; }

        diff[0] = 0f;
        for (int tau = 1; tau <= MaxLag; tau++)
        {
            float sum = 0f;
            for (int j = 0; j < Half; j++)
            {
                float d = frame[b - j] - frame[b - j - tau];
                sum += d * d;
            }
            diff[tau] = sum;
        }
        cmnd[0] = 1f;
        float running = 0f;
        for (int tau = 1; tau <= MaxLag; tau++)
        {
            running += diff[tau];
            cmnd[tau] = running > 0f ? diff[tau] * tau / running : 1f;
        }

        int best = -1;
        for (int tau = MinLag; tau < MaxLag; tau++)
        {
            if (cmnd[tau] < YinThreshold)
            {
                while (tau + 1 < MaxLag && cmnd[tau + 1] < cmnd[tau]) tau++;
                best = tau; break;
            }
        }
        if (best < 0) { Fail(); return; }

        float lag = best;
        if (best > 1 && best < MaxLag)
        {
            float s0 = cmnd[best - 1], s1 = cmnd[best], s2 = cmnd[best + 1];
            float den = s0 - 2f * s1 + s2;
            if (MathF.Abs(den) > 1e-9f) lag = best + 0.5f * (s0 - s2) / den;
        }
        float f = fs / lag;
        if (f < 40f || f > 1500f) { Fail(); return; }

        fails = 0;
        if (!Locked)
        {
            Frequency = f; Locked = true; pendingFreq = 0f;
        }
        else if (MathF.Abs(MathF.Log2(f / Frequency)) > 0.04f)
        {
            if (pendingFreq > 0f && MathF.Abs(MathF.Log2(f / pendingFreq)) < 0.04f)
            {
                Frequency = f; pendingFreq = 0f;
            }
            else pendingFreq = f;
        }
        else
        {
            pendingFreq = 0f;
            Frequency += 0.5f * (f - Frequency);
        }
    }

    void Fail()
    {
        if (++fails > MaxFails) { Locked = false; pendingFreq = 0f; }
    }
}
