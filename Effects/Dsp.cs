using System;
using System.Runtime.CompilerServices;

namespace Jrig.Effects;

/// <summary>
/// Small allocation-free DSP building blocks shared by the pedals.
/// Everything here is safe to call from the audio thread once allocated
/// (allocation only happens in Allocate/ctor, which are called from OnPrepare).
/// </summary>
internal static class Dsp
{
    public const float TwoPi = MathF.PI * 2f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DbToLin(float db) => MathF.Pow(10f, db * 0.05f);

    /// <summary>One-pole coefficient for a time constant in milliseconds (0 = instant).</summary>
    public static float TimeToCoef(float ms, float sr) => ms <= 0f ? 0f : MathF.Exp(-1f / (0.001f * ms * sr));

    /// <summary>Exponential map of t in 0..1 onto lo..hi.</summary>
    public static float ExpMap(float t, float lo, float hi) => lo * MathF.Pow(hi / lo, t);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

    /// <summary>Pade tanh approximation, exact at +-3 and clamped beyond.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Tanh(float x)
    {
        if (x > 3f) return 1f;
        if (x < -3f) return -1f;
        float x2 = x * x;
        return x * (27f + x2) / (27f + 9f * x2);
    }

    /// <summary>Linear below 0.8, tanh knee above it. Used to keep looped/overdubbed audio from blowing up.</summary>
    public static float SoftLimit(float x)
    {
        float a = MathF.Abs(x);
        if (a <= 0.8f) return x;
        float y = 0.8f + 0.2f * Tanh((a - 0.8f) * 5f);
        return x < 0f ? -y : y;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Flush(float x) => MathF.Abs(x) < 1e-18f ? 0f : x;
}

/// <summary>RBJ biquad (transposed direct form II). Coefficients may be changed from the audio thread.</summary>
internal sealed class Biquad
{
    float b0 = 1f, b1, b2, a1, a2, z1, z2;

    public void Lowpass(float f, float q, float sr) => Design(0, f, q, sr);
    public void Highpass(float f, float q, float sr) => Design(1, f, q, sr);
    /// <summary>Band-pass with 0 dB peak gain.</summary>
    public void Bandpass(float f, float q, float sr) => Design(2, f, q, sr);

    void Design(int kind, float f, float q, float sr)
    {
        f = Dsp.Clamp(f, 10f, sr * 0.45f);
        float w0 = Dsp.TwoPi * f / sr;
        float c = MathF.Cos(w0), s = MathF.Sin(w0);
        float alpha = s / (2f * q);
        float nb0, nb1, nb2;
        switch (kind)
        {
            case 0: nb0 = (1f - c) * 0.5f; nb1 = 1f - c; nb2 = nb0; break;
            case 1: nb0 = (1f + c) * 0.5f; nb1 = -(1f + c); nb2 = nb0; break;
            default: nb0 = alpha; nb1 = 0f; nb2 = -alpha; break;
        }
        float inv = 1f / (1f + alpha);
        b0 = nb0 * inv; b1 = nb1 * inv; b2 = nb2 * inv;
        a1 = -2f * c * inv; a2 = (1f - alpha) * inv;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        float y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        if (MathF.Abs(z1) < 1e-20f) z1 = 0f;
        if (MathF.Abs(z2) < 1e-20f) z2 = 0f;
        return y;
    }

    public void Reset() { z1 = 0f; z2 = 0f; }
}

/// <summary>
/// Zero-delay-feedback state variable filter (TPT). Call Set() to change cutoff/Q, Tick() per sample,
/// then read Lp / Bp / Hp. Bp has a peak gain of Q, so Bp * K has unity peak gain.
/// </summary>
internal sealed class Svf
{
    float a1, a2, a3, k = 1f, ic1, ic2;
    public float Lp, Bp, Hp;
    public float K => k;

    public void Set(float f, float q, float sr)
    {
        f = Dsp.Clamp(f, 20f, sr * 0.45f);
        float g = MathF.Tan(MathF.PI * f / sr);
        k = 1f / q;
        a1 = 1f / (1f + g * (g + k));
        a2 = g * a1;
        a3 = g * a2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Tick(float x)
    {
        float v3 = x - ic2;
        float v1 = a1 * ic1 + a2 * v3;
        float v2 = ic2 + a2 * ic1 + a3 * v3;
        ic1 = Dsp.Flush(2f * v1 - ic1);
        ic2 = Dsp.Flush(2f * v2 - ic2);
        Lp = v2; Bp = v1; Hp = x - k * v1 - v2;
    }

    public void Reset() { ic1 = 0f; ic2 = 0f; }
}

/// <summary>One-pole parameter smoother.</summary>
internal struct Smooth
{
    float cur, coef;
    public void Init(float value, float ms, float sr) { cur = value; coef = Dsp.TimeToCoef(ms, sr); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Next(float target) { cur = target + (cur - target) * coef; return cur; }
    public float Current => cur;
    /// <summary>Jump straight to a value (used to restart a fade-in).</summary>
    public void Set(float value) => cur = value;
}

/// <summary>Sine LFO with a 0..1 phase accumulator.</summary>
internal struct Lfo
{
    float phase;
    /// <summary>Advance by one sample and return the new phase (0..1).</summary>
    public float Advance(float rateHz, float sr)
    {
        phase += rateHz / sr;
        if (phase >= 1f) phase -= 1f;
        return phase;
    }
    /// <summary>Sine of a phase given in cycles (any value, periodic).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sine(float p) => MathF.Sin(Dsp.TwoPi * p);
    public void Reset(float p = 0f) => phase = p;
}

/// <summary>Peak envelope follower with separate attack and release.</summary>
internal struct EnvFollower
{
    float env, atk, rel;
    public void Set(float attackMs, float releaseMs, float sr)
    {
        atk = Dsp.TimeToCoef(attackMs, sr);
        rel = Dsp.TimeToCoef(releaseMs, sr);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Next(float x)
    {
        float a = MathF.Abs(x);
        float c = a > env ? atk : rel;
        env = a + (env - a) * c;
        return env;
    }
    public float Value => env;
    public void Reset() => env = 0f;
}

/// <summary>
/// Circular delay line with fractional reads. Read(d) returns the sample written d samples ago,
/// relative to the NEXT Write, so Read(1) is the most recent sample. Read before Write inside a feedback loop.
/// </summary>
internal sealed class DelayLine
{
    float[] buf = new float[8];
    int mask = 7, w;

    public int Capacity => buf.Length;

    public void Allocate(int maxDelaySamples)
    {
        int n = 8;
        while (n < maxDelaySamples + 8) n <<= 1;
        buf = new float[n];
        mask = n - 1;
        w = 0;
    }

    public void Clear() { Array.Clear(buf, 0, buf.Length); w = 0; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(float x) { buf[w] = x; w = (w + 1) & mask; }

    public float ReadLinear(float d)
    {
        float p = w - d;
        int i = (int)MathF.Floor(p);
        float f = p - i;
        float a = buf[i & mask], b = buf[(i + 1) & mask];
        return a + (b - a) * f;
    }

    /// <summary>4-point Hermite interpolation. d must be at least 2 samples.</summary>
    public float ReadHermite(float d)
    {
        float p = w - d;
        int i = (int)MathF.Floor(p);
        float f = p - i;
        float xm1 = buf[(i - 1) & mask], x0 = buf[i & mask], x1 = buf[(i + 1) & mask], x2 = buf[(i + 2) & mask];
        float c1 = 0.5f * (x1 - xm1);
        float c2 = xm1 - 2.5f * x0 + 2f * x1 - 0.5f * x2;
        float c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
        return ((c3 * f + c2) * f + c1) * f + x0;
    }
}
