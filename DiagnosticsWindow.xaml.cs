using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using Jrig.Audio;

namespace Jrig;

/// <summary>
/// Live view of every stage the guitar signal passes through, a plain-language verdict about where it stops,
/// and the log. Reads <see cref="AudioEngine.Monitor"/>, which keeps running (and logging) even when this window is closed.
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private const float Present = 0.001f;

    private sealed class Row
    {
        public required TextBlock Label;
        public required ProgressBar Bar;
        public required TextBlock Value;
        public required Ellipse Dot;
    }

    private readonly AudioEngine _engine;
    private readonly DispatcherTimer _timer;
    private readonly List<Row> _rows = new();
    private string _rowKey = "";

    public DiagnosticsWindow(AudioEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        LogHeader.Text = "LOG   " + DiagLog.LogPath;
        LogBox.Text = string.Join(Environment.NewLine, DiagLog.Tail(300)) + Environment.NewLine;
        LogBox.ScrollToEnd();
        DiagLog.LineLogged += OnLine;

        ToneButton.IsChecked = engine.TestTone;
        BypassButton.IsChecked = engine.BypassPedals;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        DiagLog.LineLogged -= OnLine;
        _engine.TestTone = false;           // never leave the tone or the bypass on by accident
        _engine.BypassPedals = false;
        base.OnClosed(e);
    }

    private void OnLine(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            if (LogBox.Text.Length > 80000)
                LogBox.Text = LogBox.Text[^50000..];
            LogBox.ScrollToEnd();
        });
    }

    // ------------------------------------------------------------------ live refresh

    private static string Db(float v) => v <= 1e-7f ? "silent" : $"{20 * Math.Log10(v):F0} dB";

    private static Brush Status(float v) =>
        v <= 1e-7f ? Brushes.IndianRed : v < Present ? Brushes.Goldenrod : Brushes.LimeGreen;

    private static double Meter(float v) =>
        v <= 1e-5f ? 0 : Math.Clamp((20 * Math.Log10(v) + 60) / 60, 0, 1);

    private void Refresh()
    {
        var r = _engine.Monitor.Current;

        VerdictTitle.Text = r.Title;
        VerdictDetail.Text = r.Detail;
        VerdictBorder.Background = new SolidColorBrush(r.Level switch
        {
            VerdictLevel.Ok => Color.FromRgb(0x1B, 0x5E, 0x20),
            VerdictLevel.Info => Color.FromRgb(0x1F, 0x4E, 0x79),
            VerdictLevel.Warn => Color.FromRgb(0x8D, 0x6E, 0x00),
            VerdictLevel.Error => Color.FromRgb(0xA3, 0x1F, 0x1F),
            _ => Color.FromRgb(0x33, 0x33, 0x3A)
        });

        // Rows: device stream, each raw channel, selected, after gain, each pedal, after pedals, output.
        var names = new List<string> { "1  Device stream (callbacks)" };
        for (int c = 0; c < r.RawChannels.Length; c++)
            names.Add($"2  Device input channel {c + 1}" + (c == r.SelectedChannel ? "  <- selected" : ""));
        names.Add($"3  Selected input (Input {r.SelectedChannel + 1})");
        names.Add("4  After INPUT GAIN");
        foreach (var p in r.Pedals) names.Add($"5  Pedal: {p.Name}");
        names.Add("6  After all pedals");
        names.Add("7  Output to device");

        string key = string.Join("|", names);
        if (key != _rowKey) Rebuild(names, key);

        int i = 0;
        // 1: stream
        {
            var row = _rows[i++];
            bool alive = r.Running && r.InputCallbacksPerSec >= 1 && r.OutputCallbacksPerSec >= 1;
            row.Bar.Value = alive ? 1 : 0;
            row.Value.Text = r.Running ? $"in {r.InputCallbacksPerSec:F0}/s   out {r.OutputCallbacksPerSec:F0}/s" : "stopped";
            row.Dot.Fill = alive ? Brushes.LimeGreen : Brushes.IndianRed;
        }
        for (int c = 0; c < r.RawChannels.Length; c++) Set(_rows[i++], r.RawChannels[c], Db(r.RawChannels[c]));
        Set(_rows[i++], r.Selected, Db(r.Selected));
        Set(_rows[i++], r.AfterGain, Db(r.AfterGain));
        foreach (var p in r.Pedals)
            Set(_rows[i++], p.Out, p.Bypassed ? "off (bypassed)" : $"in {Db(p.In)}  ->  out {Db(p.Out)}", p.Bypassed);
        Set(_rows[i++], r.AfterChain, Db(r.AfterChain));
        Set(_rows[i++], r.Final, Db(r.Final));

        StatsText.Text = r.Running
            ? $"{r.Backend}\nInput: {r.InputFormat}   Output: {r.OutputFormat}   block: {r.BlockFrames} frames\n" +
              $"all-zero input blocks: {r.ZeroInputBlocks}   invalid samples: {r.NonFinite}   underruns: {r.Underruns}   overruns: {r.Overruns}"
            : "Not running.";
        FooterText.Text = r.TestTone ? "TEST TONE ON" : r.BypassPedals ? "PEDALS BYPASSED" : "";
        ToneButton.IsChecked = r.TestTone;
        BypassButton.IsChecked = r.BypassPedals;
    }

    private static void Set(Row row, float peak, string text, bool dim = false)
    {
        row.Bar.Value = Meter(peak);
        row.Value.Text = text;
        row.Dot.Fill = dim ? Brushes.DimGray : Status(peak);
    }

    private void Rebuild(List<string> names, string key)
    {
        _rowKey = key;
        _rows.Clear();
        Stages.Children.Clear();
        foreach (var n in names)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });

            var dot = new Ellipse { Width = 12, Height = 12, Fill = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = n, Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var bar = new ProgressBar
            {
                Minimum = 0, Maximum = 1, Height = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64)),
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14)), BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0)
            };
            var val = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xD6)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

            Grid.SetColumn(dot, 0); Grid.SetColumn(label, 1); Grid.SetColumn(bar, 2); Grid.SetColumn(val, 3);
            g.Children.Add(dot); g.Children.Add(label); g.Children.Add(bar); g.Children.Add(val);
            Stages.Children.Add(g);
            _rows.Add(new Row { Label = label, Bar = bar, Value = val, Dot = dot });
        }
    }

    // ------------------------------------------------------------------ buttons

    private void Tone_Click(object sender, RoutedEventArgs e) => _engine.TestTone = ToneButton.IsChecked == true;
    private void Bypass_Click(object sender, RoutedEventArgs e) => _engine.BypassPedals = BypassButton.IsChecked == true;

    private void Rescan_Click(object sender, RoutedEventArgs e) =>
        Task.Run(() => DiagLog.Info(DeviceScanner.BuildReport()));

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{DiagLog.LogPath}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "jrig"); }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(DiagLog.LogPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "jrig"); }
    }

    private async void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        var r = _engine.Monitor.Current;
        string devices = await Task.Run(DeviceScanner.BuildReport);

        var sb = new StringBuilder();
        sb.AppendLine("jrig diagnostics report " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine($"VERDICT [{r.Level}] {r.Title}");
        sb.AppendLine(r.Detail);
        sb.AppendLine($"Backend: {r.Backend}   input: {r.InputFormat}   output: {r.OutputFormat}   block: {r.BlockFrames}");
        sb.AppendLine("Raw channels: " + string.Join(", ", r.RawChannels.Select(Db)) +
                      $"   selected(ch{r.SelectedChannel + 1}): {Db(r.Selected)}   afterGain: {Db(r.AfterGain)}   afterPedals: {Db(r.AfterChain)}   output: {Db(r.Final)}");
        foreach (var p in r.Pedals) sb.AppendLine($"Pedal {p.Name}{(p.Bypassed ? " (off)" : "")}: in {Db(p.In)} out {Db(p.Out)}");
        sb.AppendLine();
        sb.AppendLine(devices);
        sb.AppendLine("=== LOG ===");
        sb.AppendLine(DiagLog.ReadAll());

        try
        {
            Clipboard.SetText(sb.ToString());
            FooterText.Text = "Report copied to the clipboard. Paste it into the chat.";
        }
        catch (Exception ex)
        {
            string path = System.IO.Path.Combine(DiagLog.LogDirectory, "report.txt");
            File.WriteAllText(path, sb.ToString());
            MessageBox.Show(this, $"Clipboard busy ({ex.Message}). Saved to:\n{path}", "jrig");
        }
    }
}
