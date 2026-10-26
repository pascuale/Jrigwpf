using System.Windows.Media;
using Jrig.Effects;

namespace Jrig.ViewModels;

/// <summary>One pedal sitting on the board: the effect plus everything the UI needs to show it.</summary>
public sealed class PedalViewModel : ObservableObject
{
    private bool _insertBefore;
    private bool _expanded;
    private double _gainReduction;
    private string _readoutHeadline = "";
    private string _readoutDetail = "";
    private double _readoutNeedle;
    private double _readoutProgress;

    public PedalViewModel(IEffect effect, string accentHex)
    {
        Effect = effect;
        Parameters = effect.Parameters.Select(p => new ParameterViewModel(p)).ToList();

        var color = (Color)ColorConverter.ConvertFromString(accentHex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        AccentBrush = brush;
    }

    public IEffect Effect { get; }
    public string Name => Effect.TypeName;
    public Brush AccentBrush { get; }
    public IReadOnlyList<ParameterViewModel> Parameters { get; }

    /// <summary>Footswitch state. True = effect is on (LED lit).</summary>
    public bool Active
    {
        get => !Effect.Bypassed;
        set
        {
            if (Effect.Bypassed == !value) return;
            Effect.Bypassed = !value;
            Raise();
        }
    }

    /// <summary>False = minimized (name + on/off only), true = full pedal with knobs. Starts minimized.</summary>
    public bool Expanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }

    /// <summary>Shows the blue drop-position bar on this pedal's left edge during drag-and-drop.</summary>
    public bool InsertBefore
    {
        get => _insertBefore;
        set => Set(ref _insertBefore, value);
    }

    // Live readout (tuner / looper) and extra buttons.
    public bool HasReadout => Effect is IPedalReadout;
    public bool HasNeedle => Effect is IPedalReadout { HasNeedle: true };
    public bool HasProgress => Effect is IPedalReadout { HasProgress: true };
    public IReadOnlyList<PedalAction> Actions => (Effect as IPedalActions)?.Actions ?? Array.Empty<PedalAction>();
    public bool HasActions => Actions.Count > 0;
    public string ReadoutHeadline { get => _readoutHeadline; private set => Set(ref _readoutHeadline, value); }
    public string ReadoutDetail { get => _readoutDetail; private set => Set(ref _readoutDetail, value); }
    public double ReadoutNeedle { get => _readoutNeedle; private set => Set(ref _readoutNeedle, value); }
    public double ReadoutProgress { get => _readoutProgress; private set => Set(ref _readoutProgress, value); }

    public bool HasMeter => Effect is IGainReductionMeter;
    public double GainReduction => _gainReduction;
    public string GainReductionText => _gainReduction < 0.05 ? "0.0 dB" : $"-{_gainReduction:F1} dB";

    /// <summary>Called by the UI timer to refresh meters.</summary>
    public void Tick()
    {
        if (Effect is IPedalReadout readout)
        {
            if (Effect.Bypassed)
            {
                ReadoutHeadline = "OFF";
                ReadoutDetail = "";
                ReadoutNeedle = 0;
            }
            else
            {
                ReadoutHeadline = readout.Headline;
                ReadoutDetail = readout.Detail;
                ReadoutNeedle = readout.Needle;
                ReadoutProgress = readout.Progress;
            }
        }

        if (Effect is not IGainReductionMeter meter) return;

        double v = meter.GainReductionDb;
        if (Math.Abs(v - _gainReduction) < 0.02) return;

        _gainReduction = v;
        Raise(nameof(GainReduction));
        Raise(nameof(GainReductionText));
    }
}
