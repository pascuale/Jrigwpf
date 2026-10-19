namespace Jrig.Effects;

/// <summary>
/// Every pedal implements this. Audio is processed as a mono block, in place.
/// Process() runs on the audio thread: no allocations, locks, file access or UI calls.
/// </summary>
public interface IEffect
{
    /// <summary>Matches the name in <see cref="EffectCatalog"/>; used for saving presets.</summary>
    string TypeName { get; }

    /// <summary>True = pedal is switched off (signal passes through untouched).</summary>
    bool Bypassed { get; set; }

    IReadOnlyList<EffectParameter> Parameters { get; }

    /// <summary>Called before audio starts (and when a pedal is added to the board).</summary>
    void Prepare(int sampleRate, int maxBlockSize);

    void Process(float[] buffer, int offset, int count);
}

/// <summary>Optional: pedals that can report gain reduction for a meter.</summary>
public interface IGainReductionMeter
{
    /// <summary>Current gain reduction in dB (0 or positive).</summary>
    float GainReductionDb { get; }
}

/// <summary>
/// Optional: pedals that show a live readout on the board (tuner note + needle, looper state + progress).
/// Read from the UI thread, so implementations must expose thread-safe (volatile) snapshots.
/// </summary>
public interface IPedalReadout
{
    /// <summary>Big text, e.g. "A#2" or "RECORDING".</summary>
    string Headline { get; }

    /// <summary>Small text under it, e.g. "-12 cents   110.0 Hz" or "12.3 s".</summary>
    string Detail { get; }

    /// <summary>True if the pedal shows a tuner needle.</summary>
    bool HasNeedle { get; }

    /// <summary>Needle position, -1 (flat) .. +1 (sharp).</summary>
    float Needle { get; }

    /// <summary>True if the pedal shows a progress bar (loop position).</summary>
    bool HasProgress { get; }

    /// <summary>Progress bar value, 0..1.</summary>
    float Progress { get; }
}

/// <summary>A button shown on a pedal (the looper's Rec/Play, Stop and Clear).</summary>
public sealed record PedalAction(string Label, Action Invoke);

/// <summary>Optional: pedals that want extra buttons on the board.</summary>
public interface IPedalActions
{
    IReadOnlyList<PedalAction> Actions { get; }
}
