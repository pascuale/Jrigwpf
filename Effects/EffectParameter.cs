using System.Globalization;

namespace Jrig.Effects;

public enum ParameterCurve
{
    Linear,
    Logarithmic
}

/// <summary>
/// One knob-able value on a pedal. The UI thread writes <see cref="Value"/>,
/// the audio thread reads it (volatile, so no locks are needed).
/// </summary>
public sealed class EffectParameter
{
    private volatile float _value;

    public EffectParameter(string id, string name, float min, float max, float defaultValue,
        string unit = "", ParameterCurve curve = ParameterCurve.Linear, int decimals = 1)
    {
        if (curve == ParameterCurve.Logarithmic && min <= 0f)
            throw new ArgumentException("Logarithmic parameters need a positive minimum.", nameof(min));

        Id = id;
        Name = name;
        Min = min;
        Max = max;
        Default = defaultValue;
        Unit = unit;
        Curve = curve;
        Decimals = decimals;
        _value = Math.Clamp(defaultValue, min, max);
    }

    /// <summary>Stable key used when saving presets.</summary>
    public string Id { get; }
    public string Name { get; }
    public float Min { get; }
    public float Max { get; }
    public float Default { get; }
    public string Unit { get; }
    public ParameterCurve Curve { get; }
    public int Decimals { get; }

    public float Value
    {
        get => _value;
        set => _value = float.IsFinite(value) ? Math.Clamp(value, Min, Max) : Default;
    }

    /// <summary>Maps a real value to 0..1 for the knob.</summary>
    public float ToNormalized(float v)
    {
        v = Math.Clamp(v, Min, Max);
        if (Max <= Min) return 0f;
        return Curve == ParameterCurve.Logarithmic
            ? MathF.Log(v / Min) / MathF.Log(Max / Min)
            : (v - Min) / (Max - Min);
    }

    /// <summary>Maps a knob position 0..1 back to a real value.</summary>
    public float FromNormalized(float n)
    {
        n = Math.Clamp(n, 0f, 1f);
        return Curve == ParameterCurve.Logarithmic
            ? Min * MathF.Pow(Max / Min, n)
            : Min + n * (Max - Min);
    }

    public string Format(float v) =>
        v.ToString("F" + Decimals, CultureInfo.InvariantCulture) + Unit;
}
