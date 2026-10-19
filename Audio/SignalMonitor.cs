using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Jrig.Audio;

public enum VerdictLevel { Idle, Ok, Info, Warn, Error }

public sealed record PedalReading(string Name, bool Bypassed, float In, float Out);

/// <summary>What the signal looked like over the last ~3 seconds. Immutable; safe to hand to the UI thread.</summary>
public sealed class ProbeReading
{
    public bool Running;
    public string Backend = "";
    public string InputFormat = "";
    public string OutputFormat = "";
    public int BlockFrames;
    public double Seconds;                    // how long audio has been running
    public double InputCallbacksPerSec, OutputCallbacksPerSec;
    public long InputCallbacks, OutputCallbacks, ZeroInputBlocks, NonFinite, Underruns, Overruns;
    public float[] RawChannels = Array.Empty<float>();   // 3 s peak of every channel the device delivers
    public int SelectedChannel;
    public float Selected, AfterGain, AfterChain, Final;  // 3 s peaks
    public float SelectedNow, AfterGainNow, AfterChainNow, FinalNow;  // last 100 ms (for meters)
    public float[] RawChannelsNow = Array.Empty<float>();
    public PedalReading[] Pedals = Array.Empty<PedalReading>();
    public bool TestTone, BypassPedals;
    public VerdictLevel Level = VerdictLevel.Idle;
    public string Title = "Audio is stopped";
    public string Detail = "Press Start, then open this window again (or leave it open).";
}

/// <summary>
/// The "where does the signal die" check. Every 100 ms it reads and resets the audio threads' peak meters,
/// keeps a 3-second rolling maximum for each stage of the signal path, works out which stage the signal
/// stops at, and writes the result to the log (once a second, and whenever the verdict changes).
/// It runs on a thread-pool timer, never on an audio thread.
/// </summary>
public sealed class SignalMonitor : IDisposable
{
    private const int Window = 30;                 // 30 x 100 ms
    private const float Present = 0.001f;          // -60 dBFS: a plucked string is far above this
    private readonly AudioEngine _engine;
    private Timer? _timer;
    private readonly object _lock = new();

    private sealed class Roll
    {
        private readonly float[] _v = new float[Window];
        private int _i;
        public float Last;
        public void Push(float x) { _v[_i] = x; _i = (_i + 1) % Window; Last = x; }
        public float Max() { float m = 0; foreach (var x in _v) if (x > m) m = x; return m; }
    }

    private readonly Roll[] _raw = Enumerable.Range(0, SignalProbe.MaxChannels).Select(_ => new Roll()).ToArray();
    private readonly Roll _sel = new(), _gain = new(), _chain = new(), _final = new();
    private readonly Dictionary<ChainSlot, (Roll In, Roll Out)> _pedals = new();

    private long _lastIn, _lastOut, _lastTickMs;
    private double _inRate, _outRate;
    private long _startMs;
    private int _ticks;
    private string _lastVerdictKey = "";
    private long _lastUnder, _lastOver;

    public ProbeReading Current { get; private set; } = new();

    public SignalMonitor(AudioEngine engine) { _engine = engine; }

    public void Start()
    {
        lock (_lock)
        {
            Stop();
            foreach (var r in _raw) { for (int i = 0; i < Window; i++) r.Push(0); }
            foreach (var r in new[] { _sel, _gain, _chain, _final }) { for (int i = 0; i < Window; i++) r.Push(0); }
            _pedals.Clear();
            _lastIn = _lastOut = 0; _inRate = _outRate = 0; _ticks = 0; _lastVerdictKey = "";
            _lastUnder = _lastOver = 0;
            _startMs = _lastTickMs = Environment.TickCount64;
            _timer = new Timer(_ => Tick(), null, 100, 100);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            Current = new ProbeReading();
        }
    }

    public void Dispose() => Stop();

    private void Tick()
    {
        try
        {
            lock (_lock)
            {
                if (_timer == null) return;
                Sample();
            }
        }
        catch (Exception ex)
        {
            DiagLog.Error("Signal monitor error", ex);
        }
    }

    private void Sample()
    {
        var p = _engine.Probe;
        long now = Environment.TickCount64;

        for (int i = 0; i < SignalProbe.MaxChannels; i++) _raw[i].Push(SignalProbe.Take(ref p.RawChannelPeak[i]));
        _sel.Push(SignalProbe.Take(ref p.SelectedPeak));
        _gain.Push(SignalProbe.Take(ref p.AfterGainPeak));
        _chain.Push(SignalProbe.Take(ref p.AfterChainPeak));
        _final.Push(SignalProbe.Take(ref p.FinalPeak));

        var slots = _engine.Chain.Slots;
        foreach (var dead in _pedals.Keys.Where(k => !slots.Contains(k)).ToList()) _pedals.Remove(dead);
        foreach (var s in slots)
        {
            if (!_pedals.TryGetValue(s, out var r)) _pedals[s] = r = (new Roll(), new Roll());
            r.In.Push(SignalProbe.Take(ref s.InPeak));
            r.Out.Push(SignalProbe.Take(ref s.OutPeak));
        }

        long inCb = Interlocked.Read(ref p.InputCallbacks);
        long outCb = Interlocked.Read(ref p.OutputCallbacks);
        double dt = Math.Max(1, now - _lastTickMs) / 1000.0;
        // smooth over ~1 s
        _inRate = _inRate * 0.9 + (inCb - _lastIn) / dt * 0.1;
        _outRate = _outRate * 0.9 + (outCb - _lastOut) / dt * 0.1;
        _lastIn = inCb; _lastOut = outCb; _lastTickMs = now;

        int nCh = Math.Clamp(p.InputChannels, 0, SignalProbe.MaxChannels);
        var r2 = new ProbeReading
        {
            Running = _engine.IsRunning,
            Backend = p.Backend,
            InputFormat = p.InputFormat,
            OutputFormat = p.OutputFormat,
            BlockFrames = p.BlockFrames,
            Seconds = (now - _startMs) / 1000.0,
            InputCallbacksPerSec = _inRate,
            OutputCallbacksPerSec = _outRate,
            InputCallbacks = inCb,
            OutputCallbacks = outCb,
            ZeroInputBlocks = Interlocked.Read(ref p.ZeroInputBlocks),
            NonFinite = Interlocked.Read(ref p.NonFiniteSamples),
            Underruns = Interlocked.Read(ref p.Underruns),
            Overruns = Interlocked.Read(ref p.Overruns),
            RawChannels = _raw.Take(nCh).Select(x => x.Max()).ToArray(),
            RawChannelsNow = _raw.Take(nCh).Select(x => x.Last).ToArray(),
            SelectedChannel = _engine.InputChannel,
            Selected = _sel.Max(), AfterGain = _gain.Max(), AfterChain = _chain.Max(), Final = _final.Max(),
            SelectedNow = _sel.Last, AfterGainNow = _gain.Last, AfterChainNow = _chain.Last, FinalNow = _final.Last,
            Pedals = slots.Select(s => new PedalReading(s.Effect.TypeName, s.Effect.Bypassed, _pedals[s].In.Max(), _pedals[s].Out.Max())).ToArray(),
            TestTone = _engine.TestTone,
            BypassPedals = _engine.BypassPedals,
        };
        Diagnose(r2);
        Current = r2;

        if (++_ticks % 10 == 0) LogSecond(r2);

        string key = r2.Level + "|" + r2.Title;
        if (key != _lastVerdictKey && r2.Seconds >= 1.6)
        {
            _lastVerdictKey = key;
            DiagLog.Info($"VERDICT [{r2.Level}] {r2.Title} -- {r2.Detail.Replace('\n', ' ')}");
        }
    }

    // ------------------------------------------------------------------ the verdict

    private static string Db(float v) =>
        v <= 1e-7f ? "silent (0)" : $"{20 * Math.Log10(v):F0} dBFS";

    private static void Diagnose(ProbeReading r)
    {
        if (!r.Running) return;

        void Set(VerdictLevel l, string t, string d) { r.Level = l; r.Title = t; r.Detail = d; }

        if (r.Seconds < 1.6) { Set(VerdictLevel.Info, "Measuring...", "Collecting a few seconds of signal. Strum the guitar."); return; }

        string where = r.Backend.StartsWith("ASIO") ? "the ASIO driver" : "Windows";

        if (r.InputCallbacksPerSec < 1)
        {
            Set(VerdictLevel.Error, "DEAD AT STAGE 1: the input device delivers no audio at all",
                $"jrig started the input stream but {where} is not sending any blocks. " +
                (r.Backend.StartsWith("ASIO")
                    ? "ASIO4ALL: open its panel and switch ON the USB Audio CODEC (input and output). Close Deplike/AmpliTube (they hold the device). Or switch API to Windows audio."
                    : "Another program (Deplike, AmpliTube, a DAW) probably has the device in exclusive mode. Close it, or untick Exclusive. Check the log for the exact error."));
            return;
        }

        if (r.OutputCallbacksPerSec < 1)
        {
            Set(VerdictLevel.Error, "DEAD AT STAGE 7: the output device is not asking for audio",
                "Input is flowing but nothing is being sent to the speakers. Pick a different OUT DEVICE, or close the program that has the output in exclusive mode.");
            return;
        }

        if (r.TestTone)
        {
            Set(r.Final > Present ? VerdictLevel.Info : VerdictLevel.Error,
                r.Final > Present ? "Test tone is on: the output stage is producing sound" : "Test tone is on but the output stage is silent",
                r.Final > Present
                    ? "jrig is handing a tone to the output device. If you cannot hear it, the problem is AFTER jrig: wrong OUT DEVICE, Windows volume/mute, or the headphone knob on the Guitar Link."
                    : "Output volume is at its minimum or the output device is not playing. Raise OUTPUT VOLUME.");
            return;
        }

        float rawMax = r.RawChannels.Length == 0 ? 0 : r.RawChannels.Max();

        if (rawMax <= 1e-7f)
        {
            Set(VerdictLevel.Error, "DEAD AT STAGE 2: the device delivers only digital silence (all zeros)",
                "Blocks arrive but every sample is exactly 0, so the guitar never reaches jrig. Usual causes, in order:\n" +
                "1. Windows Settings > Privacy & security > Microphone: turn ON 'Let desktop apps access your microphone'.\n" +
                "2. Windows Sound settings > Recording: 'Microphone (USB Audio CODEC)' must be enabled, not muted, volume up.\n" +
                "3. The input is the wrong device: choose the one AmpliTube uses ('Microphone (USB A...)').\n" +
                (r.Backend.StartsWith("ASIO") ? "4. ASIO4ALL: the USB Audio CODEC input is not switched on in its panel. Switch API to Windows audio instead.\n" : "4. Another program has the input in exclusive mode.\n") +
                "5. Guitar cable fully in the Guitar Link's jack; try pressing the interface's input/mono button if it has one.");
            return;
        }

        int sel = r.SelectedChannel;
        if (sel < r.RawChannels.Length && r.RawChannels[sel] <= 1e-7f && rawMax > Present)
        {
            int best = Array.IndexOf(r.RawChannels, rawMax);
            Set(VerdictLevel.Error, $"DEAD AT STAGE 3: selected Input {sel + 1} is silent but Input {best + 1} has signal",
                $"Set the INPUT box in the toolbar to 'Input {best + 1}' and press Start again.");
            return;
        }

        if (r.Selected < Present)
        {
            Set(VerdictLevel.Warn, "Input is alive but very quiet (or you are not playing)",
                $"Level at the selected input over the last 3 s: {Db(r.Selected)}. Strum hard. If it stays this low: raise INPUT GAIN, raise the Windows recording level for the Guitar Link, " +
                "and check the guitar's volume knob and the Guitar Link's own gain setting.");
            return;
        }

        // Signal has arrived. Follow it through the pedals.
        if (r.AfterGain < Present)
        {
            Set(VerdictLevel.Error, "DEAD AT STAGE 4: INPUT GAIN removes the signal",
                $"Selected input is {Db(r.Selected)} but after input gain it is {Db(r.AfterGain)}. Raise the INPUT GAIN slider.");
            return;
        }

        if (!r.BypassPedals)
        {
            foreach (var pd in r.Pedals)
            {
                if (pd.Bypassed) continue;
                if (pd.In > Present && pd.Out < pd.In * 0.01f)
                {
                    string extra = pd.Name == "Tuner"
                        ? " The Tuner mutes your sound while it is ON (by design). Switch the tuner pedal off with its footswitch, or set its Mute knob to 0."
                        : " Switch it off with its footswitch or check its knobs (level/mix at minimum, looper not playing, etc.).";
                    Set(VerdictLevel.Error, $"DEAD AT STAGE 5: the {pd.Name} pedal silences the signal",
                        $"Into {pd.Name}: {Db(pd.In)}   out of {pd.Name}: {Db(pd.Out)}." + extra);
                    return;
                }
            }
        }

        if (r.AfterChain < Present)
        {
            Set(VerdictLevel.Error, "DEAD AT STAGE 6: the signal vanishes somewhere in the pedal chain",
                $"After gain {Db(r.AfterGain)}, after pedals {Db(r.AfterChain)}. Press 'Bypass pedals' to confirm, then remove pedals one by one.");
            return;
        }

        if (r.Final < Present)
        {
            Set(VerdictLevel.Error, "DEAD AT STAGE 7: OUTPUT VOLUME (or invalid numbers) silences the signal",
                $"After pedals {Db(r.AfterChain)} but at the output {Db(r.Final)}. Raise OUTPUT VOLUME." +
                (r.NonFinite > 0 ? $" {r.NonFinite} invalid (NaN/infinity) samples were replaced by silence: a pedal is producing bad numbers." : ""));
            return;
        }

        if (r.Underruns > 20 || r.Overruns > 20)
        {
            Set(VerdictLevel.Warn, "Signal flows, but the stream is glitching",
                $"Buffer underruns: {r.Underruns}, overruns: {r.Overruns}. Try a larger buffer or untick Exclusive.");
            return;
        }

        Set(VerdictLevel.Ok, "SIGNAL IS FLOWING: guitar reaches the output device",
            $"Input {Db(r.Selected)}  ->  pedals {Db(r.AfterChain)}  ->  output {Db(r.Final)}. " +
            "If you still hear nothing, the problem is after jrig: check OUT DEVICE (must be the Guitar Link / your headphones), Windows volume and the headphone knob. 'Test tone' proves the output path.");
    }

    private void LogSecond(ProbeReading r)
    {
        var sb = new StringBuilder("STATS ");
        var ci = CultureInfo.InvariantCulture;
        sb.Append(string.Format(ci, "inCb/s={0:F0} outCb/s={1:F0} block={2} ", r.InputCallbacksPerSec, r.OutputCallbacksPerSec, r.BlockFrames));
        sb.Append("raw=[").Append(string.Join(",", r.RawChannels.Select(x => Db(x)))).Append("] ");
        sb.Append($"sel(ch{r.SelectedChannel + 1})={Db(r.Selected)} gain={Db(r.AfterGain)} chain={Db(r.AfterChain)} out={Db(r.Final)} ");
        if (r.Pedals.Length > 0)
            sb.Append("pedals=[").Append(string.Join("; ", r.Pedals.Select(x => $"{x.Name}{(x.Bypassed ? "(off)" : "")} {Db(x.In)}>{Db(x.Out)}"))).Append("] ");
        sb.Append($"zeroBlocks={r.ZeroInputBlocks} nonFinite={r.NonFinite} underrun={r.Underruns} overrun={r.Overruns}");
        if (r.TestTone) sb.Append(" TESTTONE");
        if (r.BypassPedals) sb.Append(" BYPASS");
        DiagLog.Info(sb.ToString());
    }
}
