using Jrig.Effects;

namespace Jrig.ViewModels;

/// <summary>Wraps one <see cref="EffectParameter"/> for a knob. The knob works in 0..1.</summary>
public sealed class ParameterViewModel : ObservableObject
{
    private readonly EffectParameter _parameter;
    private double _normalized;

    public ParameterViewModel(EffectParameter parameter)
    {
        _parameter = parameter;
        _normalized = parameter.ToNormalized(parameter.Value);
    }

    public string Name => _parameter.Name;

    public double DefaultNormalized => _parameter.ToNormalized(_parameter.Default);

    public double Normalized
    {
        get => _normalized;
        set
        {
            value = Math.Clamp(value, 0.0, 1.0);
            if (Set(ref _normalized, value))
            {
                _parameter.Value = _parameter.FromNormalized((float)value);
                Raise(nameof(Display));
            }
        }
    }

    /// <summary>Text under the knob, e.g. "-20.0 dB".</summary>
    public string Display => _parameter.Format(_parameter.Value);
}
