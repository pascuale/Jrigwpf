using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Jrig.Audio;
using Jrig.Controls;
using Jrig.Effects;
using Jrig.Persistence;
using Jrig.ViewModels;
using Microsoft.Win32;

namespace Jrig;

public partial class MainWindow : Window
{
    private static readonly Brush DropMarkerBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));

    private readonly AudioEngine _engine = new();      // created first: slider events can fire during InitializeComponent
    private readonly DispatcherTimer _meterTimer;
    private readonly SimulatedGuitar _sim = new();
    private string? _simFilePath;

    private sealed record SourceOption(string Label, SimulatedGuitarMode? Mode);   // Mode null = real guitar via ASIO

    private static readonly SourceOption[] Sources =
    {
        new("Guitar (Guitar Link via ASIO)", null),
        new("Simulated: riff (single notes)", SimulatedGuitarMode.Riff),
        new("Simulated: scale run", SimulatedGuitarMode.ScaleRun),
        new("Simulated: strummed chords", SimulatedGuitarMode.Chords),
        new("Simulated: dynamics (soft to hard)", SimulatedGuitarMode.Dynamics),
        new("Simulated: open strings (tuner test)", SimulatedGuitarMode.OpenStrings),
        new("Simulated: sustained drone (A2)", SimulatedGuitarMode.Drone),
        new("Simulated: single note (raw guitar, press Pick)", SimulatedGuitarMode.SingleNote),
        new("Simulated: loop an audio file...", SimulatedGuitarMode.AudioFile),
    };

    private SourceOption SelectedSource => SourceCombo.SelectedItem as SourceOption ?? Sources[0];

    private double _inputMeter;
    private double _outputMeter;
    private int _shownBufferFrames = -1;

    private Point _libraryDragStart;
    private CatalogEntry? _libraryDragEntry;

    public ObservableCollection<PedalViewModel> Pedals { get; } = new();
    public IReadOnlyList<CatalogEntry> Catalog => EffectCatalog.Entries;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        AddHandler(PedalControl.RemoveRequestedEvent, new RoutedEventHandler(OnPedalRemoveRequested));

        RateCombo.ItemsSource = new[] { 44100, 48000 };
        InputCombo.ItemsSource = new[] { "Input 1", "Input 2", "Input 3", "Input 4" };

        // Restore last session.
        bool firstRun = !File.Exists(StateStore.StatePath);
        AppState state = StateStore.LoadState();

        RateCombo.SelectedItem = state.Settings.SampleRate == 44100 ? 44100 : 48000;
        InputCombo.SelectedIndex = Math.Clamp(state.Settings.InputChannel, 0, 3);
        _simFilePath = state.Settings.SimFile;
        SourceCombo.ItemsSource = Sources;
        SourceCombo.DisplayMemberPath = nameof(SourceOption.Label);
        SourceCombo.SelectedIndex = Math.Clamp(state.Settings.SourceIndex, 0, Sources.Length - 1);
        InputGainSlider.Value = Math.Clamp(state.Settings.InputGainDb, InputGainSlider.Minimum, InputGainSlider.Maximum);
        OutputGainSlider.Value = Math.Clamp(state.Settings.OutputGainDb, OutputGainSlider.Minimum, OutputGainSlider.Maximum);
        RefreshDrivers(state.Settings.Driver);

        RestoreBoard(state.Board);
        if (firstRun && Pedals.Count == 0)
            AddPedal(new Compressor(), 0);              // so the very first launch is not an empty board

        UpdateRunningUi();

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += OnMeterTick;
        _meterTimer.Start();
    }

    // ===================================================================== audio controls

    private void RefreshDrivers_Click(object sender, RoutedEventArgs e) =>
        RefreshDrivers(DriverCombo.SelectedItem as string);

    private void RefreshDrivers(string? preferred)
    {
        string[] names;
        try
        {
            names = AudioEngine.GetDriverNames();
        }
        catch
        {
            names = Array.Empty<string>();
        }

        DriverCombo.ItemsSource = names;
        if (names.Length > 0)
            DriverCombo.SelectedItem = names.Contains(preferred) ? preferred : names[0];

        UpdateRunningUi();
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            _engine.Stop();
            UpdateRunningUi();
            return;
        }

        if (SelectedSource.Mode is { } simMode)
        {
            StartSimulated(simMode);
            return;
        }

        if (DriverCombo.SelectedItem is not string driver)
        {
            MessageBox.Show(this,
                "No ASIO driver is selected.\n\nInstall ASIO4ALL or FlexASIO (or the Behringer ASIO driver if one is available for your Guitar Link), then click Refresh.",
                "jrig", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int rate = RateCombo.SelectedItem is int r ? r : 48000;
        try
        {
            _engine.Start(driver, Math.Max(0, InputCombo.SelectedIndex), rate);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not start audio with \"{driver}\" at {rate} Hz.\n\n{ex.Message}\n\n" +
                "Things to try: switch the sample rate, close other programs using the interface, " +
                "or open Driver settings and check the interface is enabled.",
                "jrig", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UpdateRunningUi();
    }

    // ===================================================================== simulated guitar

    private void StartSimulated(SimulatedGuitarMode mode)
    {
        if (mode == SimulatedGuitarMode.AudioFile && !EnsureSimFile())
            return;

        int rate = RateCombo.SelectedItem is int r ? r : 48000;
        try
        {
            _sim.Mode = mode;
            _engine.StartSimulated(_sim, rate);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not start the simulated guitar at {rate} Hz.\n\n{ex.Message}\n\n" +
                "It plays through the Windows default output, so check a speaker or headphones is connected and not in use exclusively by another program.",
                "jrig", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UpdateRunningUi();
    }

    /// <summary>Makes sure a recording is loaded for the audio-file source, asking for one if needed.</summary>
    private bool EnsureSimFile()
    {
        if (_sim.HasFile) return true;

        if (!string.IsNullOrEmpty(_simFilePath) && File.Exists(_simFilePath))
        {
            try
            {
                _sim.LoadFile(_simFilePath);
                return true;
            }
            catch
            {
                // fall through and ask for another file
            }
        }
        return PromptForSimFile();
    }

    private bool PromptForSimFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a guitar recording to loop",
            Filter = "Audio files|*.wav;*.mp3;*.aif;*.aiff;*.flac;*.m4a|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return false;

        try
        {
            _sim.LoadFile(dialog.FileName);
            _simFilePath = dialog.FileName;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not load audio file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void LoadFile_Click(object sender, RoutedEventArgs e)
    {
        if (PromptForSimFile())
            UpdateRunningUi();
    }

    private void Pick_Click(object sender, RoutedEventArgs e) => _sim.Pick();

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        SourceOption option = SelectedSource;
        LoadFileButton.Visibility = option.Mode == SimulatedGuitarMode.AudioFile ? Visibility.Visible : Visibility.Collapsed;
        PickButton.Visibility = option.Mode == SimulatedGuitarMode.SingleNote ? Visibility.Visible : Visibility.Collapsed;

        if (_engine.IsRunning)
        {
            // Switching between simulated sources works live. Switching to or from the real guitar needs a restart.
            if (_engine.IsSimulated && option.Mode is { } mode && (mode != SimulatedGuitarMode.AudioFile || _sim.HasFile))
                _sim.Mode = mode;
            else
                _engine.Stop();
        }

        UpdateRunningUi();
    }

    private void DriverSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DriverCombo.SelectedItem is not string driver) return;
        try
        {
            _engine.ShowControlPanel(driver);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "jrig", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InputGain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        _engine.InputGainDb = (float)e.NewValue;

    private void OutputGain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        _engine.OutputGainDb = (float)e.NewValue;

    private void UpdateRunningUi()
    {
        bool running = _engine.IsRunning;

        StartButton.Content = running ? "Stop" : "Start";
        StartButton.Background = new SolidColorBrush(running ? Color.FromRgb(0xC6, 0x28, 0x28) : Color.FromRgb(0x2E, 0x7D, 0x32));

        bool sim = SelectedSource.Mode != null;

        PickButton.IsEnabled = running && _engine.IsSimulated;

        DriverCombo.IsEnabled = !running && !sim;
        InputCombo.IsEnabled = !running && !sim;
        RefreshButton.IsEnabled = !running && !sim;
        DriverSettingsButton.IsEnabled = !sim;
        RateCombo.IsEnabled = !running;

        _shownBufferFrames = -1;

        if (running && _engine.IsSimulated)
            StatusText.Text = $"Simulated guitar at {_engine.SampleRate} Hz, playing through the Windows default output. " +
                              "Delay is higher than with ASIO, which is normal for testing. Pick another SOURCE any time to change what it plays.";
        else if (running)
            StatusText.Text = $"Running at {_engine.SampleRate} Hz. Waiting for audio...";
        else if (sim)
            StatusText.Text = "Simulated guitar selected: no interface or ASIO driver needed. Press Start and listen through your speakers or headphones.";
        else if (DriverCombo.Items.Count == 0)
            StatusText.Text = "No ASIO drivers found. Install ASIO4ALL or FlexASIO (or a Behringer ASIO driver), then click Refresh.";
        else
            StatusText.Text = "Stopped. Plug in your guitar, pick the driver, then press Start.";
    }

    private void OnMeterTick(object? sender, EventArgs e)
    {
        _inputMeter = Math.Max(ToMeter(_engine.TakeInputPeak()), _inputMeter - 0.04);
        _outputMeter = Math.Max(ToMeter(_engine.TakeOutputPeak()), _outputMeter - 0.04);
        InputMeter.Value = _inputMeter;
        OutputMeter.Value = _outputMeter;

        foreach (var pedal in Pedals)
            pedal.Tick();

        int frames = _engine.BufferFrames;
        if (_engine.IsRunning && !_engine.IsSimulated && frames != _shownBufferFrames && frames > 0)
        {
            _shownBufferFrames = frames;
            double ms = frames * 1000.0 / _engine.SampleRate;
            StatusText.Text = $"Running at {_engine.SampleRate} Hz, buffer {frames} samples ({ms:F1} ms each way)";
        }
    }

    /// <summary>Peak level 0..1 -> meter position, with -60 dB at the left and 0 dB at the right.</summary>
    private static double ToMeter(float peak)
    {
        if (peak <= 1e-5f) return 0;
        double db = 20.0 * Math.Log10(peak);
        return Math.Clamp((db + 60.0) / 60.0, 0.0, 1.0);
    }

    // ===================================================================== board editing

    private void AddPedal(IEffect effect, int index)
    {
        index = Math.Clamp(index, 0, Pedals.Count);
        string accent = EffectCatalog.Find(effect.TypeName)?.AccentHex ?? "#9E9E9E";

        _engine.Chain.Insert(index, effect);
        Pedals.Insert(index, new PedalViewModel(effect, accent));
    }

    internal void RemovePedal(PedalViewModel pedal)
    {
        _engine.Chain.Remove(pedal.Effect);
        Pedals.Remove(pedal);
    }

    private void MovePedal(int from, int to)
    {
        _engine.Chain.Move(from, to);
        Pedals.Move(from, to);
    }

    private void ClearBoard()
    {
        foreach (var pedal in Pedals.ToList())
            RemovePedal(pedal);
    }

    private void OnPedalRemoveRequested(object sender, RoutedEventArgs e)
    {
        if (e.Source is FrameworkElement { DataContext: PedalViewModel pedal })
            RemovePedal(pedal);
    }

    // ===================================================================== drag and drop: board

    private void Board_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DragFormats.Pedal))
            e.Effects = DragDropEffects.Move;
        else if (e.Data.GetDataPresent(DragFormats.Catalog))
            e.Effects = DragDropEffects.Copy;
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        ShowInsertMarker(ComputeDropIndex(e));
        AutoScrollBoard(e);
        e.Handled = true;
    }

    private void Board_DragLeave(object sender, DragEventArgs e) => ShowInsertMarker(-1);

    private void Board_Drop(object sender, DragEventArgs e)
    {
        int index = ComputeDropIndex(e);
        ShowInsertMarker(-1);

        if (e.Data.GetData(DragFormats.Pedal) is PedalViewModel pedal)
        {
            int from = Pedals.IndexOf(pedal);
            if (from >= 0)
            {
                int to = index > from ? index - 1 : index;      // index is "before pedal #index" in the old order
                if (to != from)
                    MovePedal(from, to);
            }
        }
        else if (e.Data.GetData(DragFormats.Catalog) is CatalogEntry entry)
        {
            AddPedal(entry.Create(), index);
        }

        e.Handled = true;
    }

    /// <summary>Which slot a drop at the cursor would land in: before the first pedal whose middle is right of the cursor.</summary>
    private int ComputeDropIndex(DragEventArgs e)
    {
        for (int i = 0; i < Pedals.Count; i++)
        {
            if (PedalItems.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
            {
                Point p = e.GetPosition(container);
                if (p.X < container.ActualWidth / 2)
                    return i;
            }
        }
        return Pedals.Count;
    }

    private void ShowInsertMarker(int index)
    {
        for (int i = 0; i < Pedals.Count; i++)
            Pedals[i].InsertBefore = i == index;

        EndMarker.Fill = index >= 0 && index == Pedals.Count ? DropMarkerBrush : Brushes.Transparent;
    }

    private void AutoScrollBoard(DragEventArgs e)
    {
        Point p = e.GetPosition(BoardScroll);
        if (p.X < 50)
            BoardScroll.ScrollToHorizontalOffset(BoardScroll.HorizontalOffset - 25);
        else if (p.X > BoardScroll.ViewportWidth - 50)
            BoardScroll.ScrollToHorizontalOffset(BoardScroll.HorizontalOffset + 25);
    }

    // ===================================================================== drag and drop: library

    private void Library_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _libraryDragStart = e.GetPosition(null);
        _libraryDragEntry = EntryUnderMouse(e);
    }

    private void Library_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_libraryDragEntry == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _libraryDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _libraryDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var entry = _libraryDragEntry;
        _libraryDragEntry = null;
        DragDrop.DoDragDrop(LibraryList, new DataObject(DragFormats.Catalog, entry), DragDropEffects.Copy);
    }

    private void Library_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EntryUnderMouse(e) is { } entry)
            AddPedal(entry.Create(), Pedals.Count);
    }

    private void Library_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormats.Pedal) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropping a board pedal back on the library removes it.</summary>
    private void Library_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormats.Pedal) is PedalViewModel pedal)
            RemovePedal(pedal);
        e.Handled = true;
    }

    private CatalogEntry? EntryUnderMouse(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return null;
        return (ItemsControl.ContainerFromElement(LibraryList, source) as ListBoxItem)?.DataContext as CatalogEntry;
    }

    // ===================================================================== presets and persistence

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save pedalboard preset",
            Filter = "jrig preset (*.json)|*.json",
            FileName = "my-board.json"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            StateStore.SaveBoard(dialog.FileName, CaptureBoard());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save preset", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load pedalboard preset",
            Filter = "jrig preset (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            BoardState? board = StateStore.LoadBoard(dialog.FileName);
            if (board == null) return;

            ClearBoard();
            RestoreBoard(board);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not load preset", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private BoardState CaptureBoard() => new()
    {
        Pedals = Pedals.Select(p => new PedalState
        {
            Type = p.Effect.TypeName,
            Active = p.Active,
            Parameters = p.Effect.Parameters.ToDictionary(x => x.Id, x => x.Value)
        }).ToList()
    };

    private void RestoreBoard(BoardState board)
    {
        foreach (PedalState saved in board.Pedals)
        {
            CatalogEntry? entry = EffectCatalog.Find(saved.Type);
            if (entry == null) continue;                    // pedal type no longer exists: skip it

            IEffect effect = entry.Create();
            foreach (EffectParameter p in effect.Parameters)
            {
                if (saved.Parameters.TryGetValue(p.Id, out float value))
                    p.Value = value;
            }
            effect.Bypassed = !saved.Active;

            AddPedal(effect, Pedals.Count);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _meterTimer.Stop();

        StateStore.SaveState(new AppState
        {
            Board = CaptureBoard(),
            Settings = new AppSettings
            {
                Driver = DriverCombo.SelectedItem as string,
                SampleRate = RateCombo.SelectedItem is int r ? r : 48000,
                InputChannel = Math.Max(0, InputCombo.SelectedIndex),
                InputGainDb = InputGainSlider.Value,
                OutputGainDb = OutputGainSlider.Value,
                SourceIndex = Math.Max(0, SourceCombo.SelectedIndex),
                SimFile = _simFilePath
            }
        });

        _engine.Dispose();
        base.OnClosed(e);
    }
}
