using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jrig.ViewModels;

namespace Jrig.Controls;

public partial class PedalControl : UserControl
{
    /// <summary>Raised (and bubbled up to the window) when the pedal's X button is clicked.</summary>
    public static readonly RoutedEvent RemoveRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(RemoveRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PedalControl));

    private Point _dragStart;
    private bool _dragArmed;

    public PedalControl()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler RemoveRequested
    {
        add => AddHandler(RemoveRequestedEvent, value);
        remove => RemoveHandler(RemoveRequestedEvent, value);
    }

    private void Remove_Click(object sender, RoutedEventArgs e) =>
        RaiseEvent(new RoutedEventArgs(RemoveRequestedEvent, this));

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Jrig.Effects.PedalAction action })
            action.Invoke();
    }

    // Drag only starts from the header, so turning knobs never grabs the whole pedal.

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragArmed = true;
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _dragArmed = false;

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || DataContext is not PedalViewModel vm)
            return;

        Point pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArmed = false;
        DragDrop.DoDragDrop(this, new DataObject(DragFormats.Pedal, vm), DragDropEffects.Move);
    }
}
