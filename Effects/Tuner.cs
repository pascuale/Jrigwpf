using System;

namespace Jrig.Effects;

/// <summary>
/// Chromatic tuner using a YIN pitch detector on a decimated copy of the signal.
/// The UI reads Frequency, NoteName, Octave, Cents and HasSignal (all thread-safe volatile snapshots).
/// The audio path is untouched unless Mute is on (the usual "silent tuning" behavior).
/// </summary>
public sealed class Tuner : EffectBase, IPedalReadout
{

    // Detector works at ~12 kHz: plenty for guitar fundamentals (E2 82 Hz up to ~1.3 kHz).
    const int Window = 1024;            // analysis frame (decimated samples)
    const int MaxLag = 512;             // lowest detectable freq ~ 12000 / 512 = 23 Hz (more than enough)
    const int MinLag = 8;               // highest detectable ~1.5 kHz
    const float YinThreshold = 0.12f;
    const float SilenceRms = 0.004f;    // about -48 dBFS

    static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    readonly EffectParameter reference, mute;
    readonly Biquad antiAlias = new();
    readonly float[] frame = new float[Window];
    readonly float[] diff = new float[MaxLag + 1];
    readonly float[] cmnd = new float[MaxLag + 1];
    int decim = 4, decimCount, fill, hop;
    float sr = 48000f;
    float smoothFreq;

    volatile float freqOut;
    volatile int noteIndexOut = -1;     // 0..11 = C..B
    volatile int octaveOut;
    volatile float centsOut;
    volatile bool signalOut;

    public float Frequency => freqOut;
    public string NoteName => noteIndexOut < 0 ? "--" : Names[noteIndexOut];
    public int OctaveNumber => octaveOut;
    public float Cents => centsOut;
    public bool HasSignal => signalOut;

    // UI readout: big note name, detail line, needle in -1..1 (= -50..+50 cents)
    public string Headline => HasSignal ? $"{NoteName}{OctaveNumber}" : "--";
    public string Detail => HasSignal ? $"{Cents:+0;-0;0} cents   {Frequency:F1} Hz" : "play a note";
    public bool HasNeedle => true;
    public float Needle => HasSignal ? Math.Clamp(Cents / 50f, -1f, 1f) : 0f;
    public bool HasProgress => false;
    public float Progress => 0f;

    public Tuner() : base("Tuner")
    {
        reference = Add("ref", "Ref A4", 430f, 450f, 440f, " Hz", ParameterCurve.Linear, 0);
        mute = Add("mute", "Mute", 0f, 1f, 1f, "", ParameterCurve.Linear, 0);   // >= 0.5 mutes output while tuning
    }

    protected override void OnPrepare()
    {
        sr = SampleRate;
        decim = Math.Max(1, (int)MathF.Round(sr / 12000f));
        antiAlias.Lowpass(sr / decim * 0.4f, 0.707f, sr);
        antiAlias.Reset();
        decimCount = 0; fill = 0; hop = 0; smoothFreq = 0f;
        Array.Clear(frame, 0, frame.Length);
        noteIndexOut = -1; signalOut = false;
    }

    protected override void ProcessCore(float[] buf, int offset, int n)
    {
        for (int i = 0; i < n; i++)
        {
            float lp = antiAlias.Process(buf[offset + i]);
            if (++decimCount >= decim)
            {
                decimCount = 0;
                // sliding frame: shift by half a window every time it fills
                frame[fill++] = lp;
                if (fill >= Window)
                {
                    Analyze(sr / decim);
                    Array.Copy(frame, Window / 2, frame, 0, Window / 2);
                    fill = Window / 2;
                }
            }
        }
        if (mute.Value >= 0.5f) Array.Clear(buf, offset, n);
    }

    void Analyze(float fs)
    {
        // RMS gate
        double e = 0;
        for (int i = 0; i < Window; i++) e += frame[i] * frame[i];
        float rms = (float)Math.Sqrt(e / Window);
        if (rms < SilenceRms) { signalOut = false; smoothFreq = 0f; return; }

        int half = Window / 2;                      // integration length for the difference function
        int maxLag = Math.Min(MaxLag, half - 1);

        // YIN step 1-2: difference function and cumulative mean normalised difference
        diff[0] = 0f;
        for (int tau = 1; tau <= maxLag; tau++)
        {
            float sum = 0f;
            for (int j = 0; j < half; j++)
            {
                float d = frame[j] - frame[j + tau];
                sum += d * d;
            }
            diff[tau] = sum;
        }
        cmnd[0] = 1f;
        float running = 0f;
        for (int tau = 1; tau <= maxLag; tau++)
        {
            running += diff[tau];
            cmnd[tau] = running > 0f ? diff[tau] * tau / running : 1f;
        }

        // YIN step 3: first dip under threshold, then walk to the local minimum
        int best = -1;
        for (int tau = MinLag; tau < maxLag; tau++)
        {
            if (cmnd[tau] < YinThreshold)
            {
                while (tau + 1 < maxLag && cmnd[tau + 1] < cmnd[tau]) tau++;
                best = tau; break;
            }
        }
        if (best < 0) { signalOut = false; return; }

        // YIN step 4: parabolic interpolation for sub-sample lag
        float lag = best;
        if (best > 1 && best < maxLag)
        {
            float s0 = cmnd[best - 1], s1 = cmnd[best], s2 = cmnd[best + 1];
            float den = s0 - 2f * s1 + s2;
            if (MathF.Abs(den) > 1e-9f) lag = best + 0.5f * (s0 - s2) / den;
        }
        float f = fs / lag;
        if (f < 30f || f > 1500f) { signalOut = false; return; }

        // light smoothing; reset if the note jumps
        if (smoothFreq <= 0f || MathF.Abs(MathF.Log2(f / smoothFreq)) > 0.06f) smoothFreq = f;
        else smoothFreq += 0.35f * (f - smoothFreq);

        float midi = 69f + 12f * MathF.Log2(smoothFreq / reference.Value);
        int nearest = (int)MathF.Round(midi);
        float cents = (midi - nearest) * 100f;
        int idx = ((nearest % 12) + 12) % 12;

        freqOut = smoothFreq;
        centsOut = cents;
        octaveOut = nearest / 12 - 1;
        noteIndexOut = idx;
        signalOut = true;
    }
}
