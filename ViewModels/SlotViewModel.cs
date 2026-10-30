using Jrig.Effects;

namespace Jrig.ViewModels;

/// <summary>One numbered slot button on a pedal (a looper loop). Refreshed by the UI timer.</summary>
public sealed class SlotViewModel : ObservableObject
{
    private readonly IPedalSlots _slots;
    private string _stateName = "Empty";
    private bool _isSelected;
    private string _toolTip = "";

    public SlotViewModel(IPedalSlots slots, int index)
    {
        _slots = slots;
        Index = index;
        Label = (index + 1).ToString();
    }

    public int Index { get; }
    public string Label { get; }

    /// <summary>Empty, Recording, Playing, Overdub or Stopped (holds a loop). The XAML colours the button from this.</summary>
    public string StateName { get => _stateName; private set => Set(ref _stateName, value); }

    /// <summary>True for the slot the REC/PLAY, STOP and CLEAR buttons act on.</summary>
    public bool IsSelected { get => _isSelected; private set => Set(ref _isSelected, value); }

    public string ToolTip { get => _toolTip; private set => Set(ref _toolTip, value); }

    public void Press() => _slots.PressSlot(Index);

    public void Refresh(bool bypassed)
    {
        int s = _slots.SlotState(Index);
        StateName = s switch
        {
            1 => "Recording",
            2 => "Playing",
            3 => "Overdub",
            4 => "Stopped",
            _ => "Empty",
        };
        IsSelected = _slots.SelectedSlot == Index;

        float sec = _slots.SlotSeconds(Index);
        ToolTip = s == 0
            ? $"Loop {Label}: empty. Click to start recording into it."
            : $"Loop {Label}: {sec:F1} s. Click to play it. Click again to stop it.";
        if (bypassed) ToolTip += " (pedal is off)";
    }
}
