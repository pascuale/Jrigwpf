using Jrig.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace Jrig.Controls;

public partial class PedalControl : UserControl
{
    /// <summary>Raised (and bubbled up to the window) when the pedal's X button is clicked.</summary>
    public static readonly RoutedEvent RemoveRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(RemoveRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PedalControl));

    private Point _dragStart;
    private bool _dragArmed;
    private Window? _ghost;
    private Point _grabOffset;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    public PedalControl()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler RemoveRequested
    {
        add => AddHandler(RemoveRequestedEvent, value);
        remove => RemoveHandler(RemoveRequestedEvent, value);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        // Go straight to the window instead of relying on the routed event reaching it.
        if (DataContext is PedalViewModel vm && Window.GetWindow(this) is MainWindow main)
            main.RemovePedal(vm);
        else
            RaiseEvent(new RoutedEventArgs(RemoveRequestedEvent, this));
    }
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
        _grabOffset = e.GetPosition(this);
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
        _ghost = CreateGhost();
        try
        {
            _ghost.Show();
            MoveGhost();
            DragDrop.DoDragDrop(this, new DataObject(DragFormats.Pedal, vm), DragDropEffects.Move);
        }
        finally
        {
            _ghost.Close();
            _ghost = null;
        }
    }
    private Window CreateGhost()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int ph = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var bmp = new RenderTargetBitmap(pw, ph, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(this);

        var ghost = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Width = ActualWidth,
            Height = ActualHeight,
            Opacity = 0.75,                       // change to taste
            Content = new System.Windows.Controls.Image { Source = bmp, Stretch = Stretch.Fill }
        };

        // Make the ghost click-through so it never blocks the drop target underneath.
        ghost.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(ghost).Handle;
            const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        return ghost;
    }

    private void MoveGhost()
    {
        if (_ghost == null || !GetCursorPos(out var p)) return;
        var src = PresentationSource.FromVisual(this);
        Point dip = src?.CompositionTarget?.TransformFromDevice.Transform(new Point(p.X, p.Y)) ?? new Point(p.X, p.Y);
        _ghost.Left = dip.X - _grabOffset.X;
        _ghost.Top = dip.Y - _grabOffset.Y;
    }

    protected override void OnGiveFeedback(GiveFeedbackEventArgs e)
    {
        base.OnGiveFeedback(e);
        MoveGhost();
    }
}
