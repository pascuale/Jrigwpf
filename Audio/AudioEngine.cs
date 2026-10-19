using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Jrig.Audio;

/// <summary>
/// Real-time audio path: ASIO input (guitar, mono) -> input gain -> pedal chain ->
/// output gain -> both output channels.
///
/// Everything in OnAudioAvailable runs on the driver's audio thread, so it must not
/// allocate, lock, touch files or touch the UI. All buffers are allocated up front.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    /// <summary>Largest ASIO buffer (in frames) we will accept. Real buffers are 32..2048.</summary>
    public const int MaxBlock = 16384;

    private AsioOut? _asio;
    private WaveOutEvent? _waveOut;               // simulated-guitar path: plays through the Windows default output
    private volatile bool _simulated;

    // Windows audio (WASAPI) path: capture thread -> ring buffer -> output thread runs the pedals.
    private WasapiCapture? _wasapiIn;
    private WasapiOut? _wasapiOut;
    private WaveFormat? _capFormat;
    private int _capKind;                         // 0 = 16-bit PCM, 1 = 24-bit PCM, 2 = 32-bit PCM, 3 = 32-bit float
    private int _selChannel;
    private const int RingSize = 1 << 15;         // frames; power of two
    private readonly float[] _ring = new float[RingSize];
    private long _ringW, _ringR;
    private bool _ringPrimed;
    public static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    // Diagnostics
    public SignalProbe Probe { get; } = new();
    public SignalMonitor Monitor { get; }
    public volatile bool TestTone;                // play a 440 Hz tone instead of the guitar: proves the output path
    public volatile bool BypassPedals;            // skip the whole pedal chain: proves the pedals are not the problem
    private float _tonePhase;
    private volatile string? _audioThreadError;
    public int InputChannel => _selChannel;
    /// <summary>Which path is running, e.g. "Windows audio (exclusive)". Empty when stopped.</summary>
    public string BackendName => Probe.Backend;

    private readonly float[] _interleaved = new float[MaxBlock * 8];
    private readonly float[] _mono = new float[MaxBlock];
    private readonly float[] _stereo = new float[MaxBlock * 2];
    private int _outFrames;                       // frames waiting in _stereo for the driver to pick up

    private volatile float _inputGainLin = 1f;
    private volatile float _outputGainLin = 1f;
    private float _inputGainDb;
    private float _outputGainDb;

    private float _inputPeak;                     // read-and-reset by the UI timer
    private float _outputPeak;
    private volatile int _bufferFrames;

    public EffectChain Chain { get; } = new();

    public AudioEngine() { Monitor = new SignalMonitor(this); }

    public bool IsRunning => _asio != null || _waveOut != null || _wasapiIn != null;

    /// <summary>True while the simulated guitar (not the ASIO interface) is feeding the pedals.</summary>
    public bool IsSimulated => _simulated;
    public int SampleRate { get; private set; } = 48000;

    /// <summary>Buffer size the driver is using, in frames (0 until audio is flowing).</summary>
    public int BufferFrames => _bufferFrames;

    public float InputGainDb
    {
        get => _inputGainDb;
        set { _inputGainDb = value; _inputGainLin = MathF.Pow(10f, value / 20f); }
    }

    public float OutputGainDb
    {
        get => _outputGainDb;
        set { _outputGainDb = value; _outputGainLin = MathF.Pow(10f, value / 20f); }
    }

    public static string[] GetDriverNames() => AsioOut.GetDriverNames();

    /// <summary>
    /// Starts audio. Call from the UI thread (many ASIO drivers insist on that).
    /// <paramref name="inputChannel"/> is zero-based. Throws if the driver can't be opened.
    /// </summary>
    public void Start(string driverName, int inputChannel, int sampleRate)
    {
        Stop();
        PrepareProbe($"ASIO: {driverName}", inputChannel);
        DiagLog.Info($"Starting ASIO driver \"{driverName}\" at {sampleRate} Hz, input channel offset {inputChannel}");

        var asio = new AsioOut(driverName);
        try
        {
            asio.InputChannelOffset = inputChannel;
            SampleRate = sampleRate;
            Chain.Prepare(sampleRate, MaxBlock);

            _bufferFrames = 0;
            _outFrames = 0;

            var provider = new OutputProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2), this);
            asio.AudioAvailable += OnAudioAvailable;
            asio.InitRecordAndPlayback(provider, 1, sampleRate);   // 1 input channel (mono guitar)
            asio.Play();
        }
        catch (Exception ex)
        {
            DiagLog.Error($"ASIO start failed ({driverName})", ex);
            asio.AudioAvailable -= OnAudioAvailable;
            asio.Dispose();
            Probe.Backend = "";
            throw;
        }

        _asio = asio;
        DiagLog.Info($"ASIO started. Driver input channels: {asio.DriverInputChannelCount}, output channels: {asio.DriverOutputChannelCount}, " +
                     $"driver reports buffer {asio.PlaybackLatency} samples latency");
        Monitor.Start();
    }

    private void PrepareProbe(string backend, int inputChannel)
    {
        Probe.ResetAll();
        Probe.Backend = backend;
        _selChannel = inputChannel;
        _audioThreadError = null;
        _tonePhase = 0f;
        _ringW = _ringR = 0;
        _ringPrimed = false;
    }

    /// <summary>
    /// Runs the pedals from a <see cref="SimulatedGuitar"/> instead of the ASIO interface, and plays the result
    /// through the Windows default output. No guitar, no Guitar Link and no ASIO driver needed.
    /// The extra output latency (about 100 ms) is normal here; this path is for testing sound, not timing.
    /// </summary>
    public void StartSimulated(SimulatedGuitar guitar, int sampleRate)
    {
        Stop();
        PrepareProbe("Simulated guitar", 0);

        SampleRate = sampleRate;
        guitar.Prepare(sampleRate);
        Chain.Prepare(sampleRate, MaxBlock);
        _bufferFrames = 0;
        _outFrames = 0;

        var provider = new SimulatedProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2), this, guitar);
        var output = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 };
        try
        {
            output.Init(provider);
            output.Play();
        }
        catch
        {
            output.Dispose();
            throw;
        }

        _simulated = true;
        _waveOut = output;
        Monitor.Start();
    }

    public void Stop()
    {
        bool wasRunning = IsRunning;
        Monitor.Stop();

        var wIn = _wasapiIn;
        var wOut = _wasapiOut;
        _wasapiIn = null;
        _wasapiOut = null;
        if (wIn != null)
        {
            try { wIn.DataAvailable -= OnCaptureData; wIn.RecordingStopped -= OnCaptureStopped; wIn.StopRecording(); } catch { /* device gone */ }
            try { wIn.Dispose(); } catch { /* ignore */ }
        }
        if (wOut != null)
        {
            try { wOut.Stop(); } catch { /* device gone */ }
            try { wOut.Dispose(); } catch { /* ignore */ }
        }
        if (wIn != null || wOut != null) _bufferFrames = 0;
        if (wasRunning) DiagLog.Info("Audio stopped.");

        var waveOut = _waveOut;
        _waveOut = null;
        _simulated = false;
        if (waveOut != null)
        {
            try { waveOut.Stop(); } catch { /* device already gone */ }
            waveOut.Dispose();
            _bufferFrames = 0;
        }

        var asio = _asio;
        _asio = null;
        if (asio == null) return;

        asio.AudioAvailable -= OnAudioAvailable;
        try { asio.Stop(); } catch { /* driver already gone */ }
        asio.Dispose();
        _outFrames = 0;
        _bufferFrames = 0;
    }

    // ================================================================= Windows audio (WASAPI)

    /// <summary>
    /// Windows audio path, the same kind Deplike and AmpliTube use: records from a Windows input device
    /// (e.g. "Microphone (USB Audio CODEC)") and plays to a Windows output device
    /// (e.g. "Speakers (USB Audio CODEC)"). Needs no ASIO driver. Exclusive mode gives the lowest delay.
    /// </summary>
    public void StartWasapi(string captureId, string renderId, bool exclusive, int sampleRate, int inputChannel)
    {
        Stop();
        PrepareProbe(exclusive ? "Windows audio (exclusive requested)" : "Windows audio (shared)", inputChannel);

        using var en = new MMDeviceEnumerator();
        MMDevice capDev = en.GetDevice(captureId);
        MMDevice renDev = en.GetDevice(renderId);
        DiagLog.Info($"Starting Windows audio. In: \"{capDev.FriendlyName}\"  Out: \"{renDev.FriendlyName}\"  " +
                     $"exclusive requested: {exclusive}  rate: {sampleRate}  input channel: {inputChannel + 1}");

        try
        {
            _wasapiIn = OpenCapture(capDev, exclusive, sampleRate, out bool capExclusive);
            SampleRate = _capFormat!.SampleRate;
            Probe.InputChannels = _capFormat.Channels;
            Probe.InputFormat = DeviceScanner.Describe(_capFormat) + (capExclusive ? " (exclusive)" : " (shared)");
            Chain.Prepare(SampleRate, MaxBlock);

            var provider = new WasapiRenderProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2), this);
            _wasapiOut = OpenRender(renDev, provider, exclusive, out bool outExclusive);
            Probe.OutputFormat = DeviceScanner.Describe(provider.WaveFormat) + (outExclusive ? " (exclusive)" : " (shared)");
            Probe.Backend = $"Windows audio (in {(capExclusive ? "exclusive" : "shared")}, out {(outExclusive ? "exclusive" : "shared")})";
            DiagLog.Info($"Windows audio running: {Probe.Backend}. Input {Probe.InputFormat}; output {Probe.OutputFormat}");
        }
        catch (Exception ex)
        {
            DiagLog.Error("Windows audio start failed", ex);
            Stop();
            throw;
        }

        Monitor.Start();
    }

    private WasapiCapture OpenCapture(MMDevice dev, bool exclusive, int rate, out bool gotExclusive)
    {
        gotExclusive = false;
        if (exclusive)
        {
            int[] rates = rate == 44100 ? new[] { 44100, 48000 } : new[] { 48000, 44100 };
            foreach (int r in rates)
            foreach (int ch in new[] { 1, 2 })
            foreach (int bits in new[] { 16, 24, 32 })
            {
                var fmt = new WaveFormat(r, bits, ch);
                bool ok;
                try { using var c = dev.AudioClient; ok = c.IsFormatSupported(AudioClientShareMode.Exclusive, fmt); }
                catch { ok = false; }
                if (!ok) continue;

                foreach (int ms in new[] { 10, 20, 40 })
                {
                    WasapiCapture? cap = null;
                    try
                    {
                        cap = new WasapiCapture(dev, true, ms) { ShareMode = AudioClientShareMode.Exclusive, WaveFormat = fmt };
                        HookCapture(cap, fmt);
                        cap.StartRecording();
                        gotExclusive = true;
                        DiagLog.Info($"Input opened EXCLUSIVE: {DeviceScanner.Describe(fmt)}, buffer {ms} ms");
                        return cap;
                    }
                    catch (Exception ex)
                    {
                        DiagLog.Warn($"Exclusive input {DeviceScanner.Describe(fmt)} buffer {ms} ms failed: {ex.Message}");
                        UnhookCapture(cap);
                    }
                }
            }
            DiagLog.Warn("Exclusive input could not be opened (another program may be using the device, or " +
                         "'Allow applications to take exclusive control' is off). Falling back to shared mode.");
        }

        WasapiCapture? shared = null;
        try
        {
            shared = new WasapiCapture(dev, true, 20);
            HookCapture(shared, shared.WaveFormat);
            shared.StartRecording();
            DiagLog.Info($"Input opened SHARED: {DeviceScanner.Describe(shared.WaveFormat)}");
            return shared;
        }
        catch (Exception ex)
        {
            UnhookCapture(shared);
            throw new InvalidOperationException(
                $"Could not open the input device \"{dev.FriendlyName}\": {ex.Message}\n" +
                "Close other audio programs (Deplike, AmpliTube) and check Windows Settings > Privacy & security > Microphone.", ex);
        }
    }

    private void HookCapture(WasapiCapture cap, WaveFormat fmt)
    {
        _capFormat = fmt;
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);
        _capKind = isFloat ? 3 : fmt.BitsPerSample switch { 16 => 0, 24 => 1, _ => 2 };
        _ringW = _ringR = 0;
        _ringPrimed = false;
        cap.DataAvailable += OnCaptureData;
        cap.RecordingStopped += OnCaptureStopped;
    }

    private void UnhookCapture(WasapiCapture? cap)
    {
        if (cap == null) return;
        try { cap.DataAvailable -= OnCaptureData; cap.RecordingStopped -= OnCaptureStopped; cap.Dispose(); } catch { /* ignore */ }
    }

    private WasapiOut OpenRender(MMDevice dev, IWaveProvider provider, bool exclusive, out bool gotExclusive)
    {
        gotExclusive = false;
        if (exclusive)
        {
            foreach (int ms in new[] { 10, 20, 40 })
            {
                WasapiOut? o = null;
                try
                {
                    o = new WasapiOut(dev, AudioClientShareMode.Exclusive, true, ms);
                    o.PlaybackStopped += OnRenderStopped;
                    o.Init(provider);
                    o.Play();
                    gotExclusive = true;
                    DiagLog.Info($"Output opened EXCLUSIVE, latency {ms} ms");
                    return o;
                }
                catch (Exception ex)
                {
                    DiagLog.Warn($"Exclusive output with {ms} ms failed: {ex.Message}");
                    try { if (o != null) { o.PlaybackStopped -= OnRenderStopped; o.Dispose(); } } catch { /* ignore */ }
                }
            }
            DiagLog.Warn("Exclusive output could not be opened. Falling back to shared mode.");
        }

        var shared = new WasapiOut(dev, AudioClientShareMode.Shared, true, 30);
        shared.PlaybackStopped += OnRenderStopped;
        try
        {
            shared.Init(provider);
            shared.Play();
        }
        catch (Exception ex)
        {
            shared.PlaybackStopped -= OnRenderStopped;
            shared.Dispose();
            throw new InvalidOperationException($"Could not open the output device \"{dev.FriendlyName}\": {ex.Message}", ex);
        }
        DiagLog.Info("Output opened SHARED, latency 30 ms");
        return shared;
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) DiagLog.Error("Input device stopped with an error (unplugged?)", e.Exception);
        else DiagLog.Info("Input device stream stopped.");
    }

    private void OnRenderStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) DiagLog.Error("Output device stopped with an error (unplugged?)", e.Exception);
        else DiagLog.Info("Output device stream stopped.");
    }

    /// <summary>Capture thread: decode the chosen channel into the ring buffer and note every channel's level.</summary>
    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        try
        {
            var fmt = _capFormat;
            if (fmt == null) return;
            int ch = fmt.Channels;
            int bytes = Math.Max(1, fmt.BitsPerSample / 8);
            int frameBytes = ch * bytes;
            int frames = e.BytesRecorded / frameBytes;
            byte[] b = e.Buffer;
            int sel = Math.Min(_selChannel, ch - 1);
            int kind = _capKind;
            long w = _ringW;
            bool allZero = true;
            Span<float> peaks = stackalloc float[SignalProbe.MaxChannels];

            for (int f = 0; f < frames; f++)
            {
                int o = f * frameBytes;
                for (int c = 0; c < ch; c++)
                {
                    int p = o + c * bytes;
                    float v = kind switch
                    {
                        0 => (short)(b[p] | (b[p + 1] << 8)) / 32768f,
                        1 => ((b[p] << 8) | (b[p + 1] << 16) | (b[p + 2] << 24)) / 2147483648f,
                        2 => (b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)) / 2147483648f,
                        _ => BitConverter.ToSingle(b, p)
                    };
                    if (c < SignalProbe.MaxChannels)
                    {
                        float a = v < 0f ? -v : v;
                        if (a > peaks[c]) peaks[c] = a;
                    }
                    if (v != 0f) allZero = false;
                    if (c == sel) _ring[(int)(w & (RingSize - 1))] = v;
                }
                w++;
            }

            Volatile.Write(ref _ringW, w);
            for (int c = 0; c < ch && c < SignalProbe.MaxChannels; c++)
                SignalProbe.Max(ref Probe.RawChannelPeak[c], peaks[c]);
            if (frames > 0 && allZero) Interlocked.Increment(ref Probe.ZeroInputBlocks);
            Interlocked.Increment(ref Probe.InputCallbacks);
            Interlocked.Add(ref Probe.InputFrames, frames);
            Probe.LastInputTicks = Stopwatch.GetTimestamp();
        }
        catch (Exception ex)
        {
            _audioThreadError = ex.ToString();
        }
    }

    /// <summary>Output thread: pulls guitar samples from the ring buffer, runs the pedals, hands audio to the device.</summary>
    private sealed class WasapiRenderProvider : IWaveProvider
    {
        private readonly AudioEngine _e;
        public WasapiRenderProvider(WaveFormat format, AudioEngine engine) { WaveFormat = format; _e = engine; }
        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var e = _e;
            try
            {
                const int frameBytes = 2 * sizeof(float);
                int frames = count / frameBytes;
                int done = 0;
                while (done < frames)
                {
                    int n = Math.Min(frames - done, MaxBlock);
                    long w = Volatile.Read(ref e._ringW);
                    long r = e._ringR;
                    long avail = w - r;

                    // Keep the delay bounded: if the capture side ran ahead, skip old audio.
                    long maxAvail = Math.Min(6L * n, RingSize - 2048);
                    if (avail > maxAvail)
                    {
                        r = w - 3L * n;
                        avail = 3L * n;
                        Interlocked.Increment(ref e.Probe.Overruns);
                    }

                    if (!e._ringPrimed)
                    {
                        if (avail >= 2L * n) e._ringPrimed = true;   // build a small cushion before playing
                    }

                    if (e._ringPrimed && avail >= n)
                    {
                        for (int i = 0; i < n; i++)
                            e._mono[i] = e._ring[(int)((r + i) & (RingSize - 1))];
                        e._ringR = r + n;
                        e.RunBlock(n);
                    }
                    else
                    {
                        if (e._ringPrimed)
                        {
                            Interlocked.Increment(ref e.Probe.Underruns);
                            e._ringPrimed = false;
                        }
                        e._ringR = r;
                        Array.Clear(e._stereo, 0, n * 2);
                    }

                    Buffer.BlockCopy(e._stereo, 0, buffer, offset + done * frameBytes, n * frameBytes);
                    done += n;
                }

                e._bufferFrames = frames;
                e.Probe.BlockFrames = frames;
                Interlocked.Increment(ref e.Probe.OutputCallbacks);
                Interlocked.Add(ref e.Probe.OutputFrames, frames);
                e.Probe.LastOutputTicks = Stopwatch.GetTimestamp();
                return frames * frameBytes;
            }
            catch (Exception ex)
            {
                e._audioThreadError = ex.ToString();
                Array.Clear(buffer, offset, count);
                return count;
            }
        }
    }

    /// <summary>Set if an audio thread hit an exception; the UI timer logs it (audio threads must not log).</summary>
    public string? TakeAudioThreadError() => Interlocked.Exchange(ref _audioThreadError, null);

    /// <summary>Opens the driver's own settings window (this is where buffer size is set).</summary>
    public void ShowControlPanel(string driverName)
    {
        if (_asio != null)
        {
            _asio.ShowControlPanel();
            return;
        }

        using var temp = new AsioOut(driverName);
        temp.ShowControlPanel();
    }

    /// <summary>Highest input sample since the last call (0..1+). UI meter.</summary>
    public float TakeInputPeak() => Interlocked.Exchange(ref _inputPeak, 0f);

    /// <summary>Highest output sample since the last call (0..1). UI meter.</summary>
    public float TakeOutputPeak() => Interlocked.Exchange(ref _outputPeak, 0f);

    // ---------------------------------------------------------------- audio thread

    private void OnAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
    {
        try
        {
            int frames = e.SamplesPerBuffer;
            int channels = e.InputBuffers.Length;
            Interlocked.Increment(ref Probe.InputCallbacks);
            Interlocked.Increment(ref Probe.OutputCallbacks);
            Probe.LastInputTicks = Stopwatch.GetTimestamp();
            Probe.InputChannels = channels;
            Probe.BlockFrames = frames;

            if (channels < 1 || frames > MaxBlock || frames * channels > _interleaved.Length)
            {
                _outFrames = 0;                       // unexpected buffer shape: output silence
                return;
            }

            e.GetAsInterleavedSamples(_interleaved);

            // Take the first (only) input channel; note the level of every channel for the diagnostics.
            bool allZero = true;
            for (int c = 0; c < channels && c < SignalProbe.MaxChannels; c++)
            {
                float pk = 0f;
                for (int i = 0; i < frames; i++)
                {
                    float a = _interleaved[i * channels + c];
                    if (a < 0f) a = -a;
                    if (a > pk) pk = a;
                }
                if (pk > 0f) allZero = false;
                SignalProbe.Max(ref Probe.RawChannelPeak[c], pk);
            }
            if (allZero) Interlocked.Increment(ref Probe.ZeroInputBlocks);

            for (int i = 0; i < frames; i++)
                _mono[i] = _interleaved[i * channels];

            RunBlock(frames);

            Interlocked.Add(ref Probe.InputFrames, frames);
            Interlocked.Add(ref Probe.OutputFrames, frames);
            _bufferFrames = frames;
            _outFrames = frames;
        }
        catch (Exception ex)
        {
            _audioThreadError = ex.ToString();
            _outFrames = 0;
        }
    }

    /// <summary>
    /// Shared by the ASIO and simulated paths. <see cref="_mono"/> holds <paramref name="frames"/> raw input
    /// samples; this applies input gain, the pedal chain and output gain, and fills <see cref="_stereo"/>.
    /// </summary>
    private void RunBlock(int frames)
    {
        float inGain = _inputGainLin;
        float inPeak = 0f;
        float rawPeak = 0f;
        for (int i = 0; i < frames; i++)
        {
            float raw = _mono[i];
            float ra = raw < 0f ? -raw : raw;
            if (ra > rawPeak) rawPeak = ra;
            float s = raw * inGain;
            _mono[i] = s;
            float a = s < 0f ? -s : s;
            if (a > inPeak) inPeak = a;
        }
        SignalProbe.Max(ref Probe.SelectedPeak, rawPeak);
        if (_simulated) SignalProbe.Max(ref Probe.RawChannelPeak[0], rawPeak);
        SignalProbe.Max(ref Probe.AfterGainPeak, inPeak);

        // The pedalboard (can be skipped from the diagnostics window to prove the pedals are not the problem).
        if (!BypassPedals)
            Chain.Process(_mono, 0, frames);
        SignalProbe.Max(ref Probe.AfterChainPeak, EffectChain.Peak(_mono, 0, frames));

        // Output gain, safety clamp, mono -> stereo.
        float outGain = _outputGainLin;
        float outPeak = 0f;
        bool tone = TestTone;
        float phase = _tonePhase;
        float phaseInc = 440f / SampleRate;
        long bad = 0;
        for (int i = 0; i < frames; i++)
        {
            float y = _mono[i] * outGain;
            if (float.IsNaN(y) || float.IsInfinity(y)) { y = 0f; bad++; }
            if (tone)
            {
                y = 0.25f * MathF.Sin(2f * MathF.PI * phase);
                phase += phaseInc; if (phase >= 1f) phase -= 1f;
            }
            if (y > 1f) y = 1f;
            else if (y < -1f) y = -1f;

            _stereo[2 * i] = y;
            _stereo[2 * i + 1] = y;

            float a = y < 0f ? -y : y;
            if (a > outPeak) outPeak = a;
        }

        _tonePhase = phase;
        if (bad > 0) Interlocked.Add(ref Probe.NonFiniteSamples, bad);
        SignalProbe.Max(ref Probe.FinalPeak, outPeak);

        if (inPeak > _inputPeak) _inputPeak = inPeak;
        if (outPeak > _outputPeak) _outputPeak = outPeak;
    }

    /// <summary>Simulated-guitar path: the Windows output asks for audio, we render guitar and run it through the pedals.</summary>
    private sealed class SimulatedProvider : IWaveProvider
    {
        private readonly AudioEngine _engine;
        private readonly SimulatedGuitar _guitar;

        public SimulatedProvider(WaveFormat format, AudioEngine engine, SimulatedGuitar guitar)
        {
            WaveFormat = format;
            _engine = engine;
            _guitar = guitar;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            const int frameBytes = 2 * sizeof(float);
            int frames = count / frameBytes;
            int done = 0;
            while (done < frames)
            {
                int n = Math.Min(frames - done, MaxBlock);
                _guitar.Render(_engine._mono, n);
                _engine.RunBlock(n);
                Buffer.BlockCopy(_engine._stereo, 0, buffer, offset + done * frameBytes, n * frameBytes);
                done += n;
            }
            _engine._bufferFrames = frames;
            Interlocked.Increment(ref _engine.Probe.InputCallbacks);
            Interlocked.Increment(ref _engine.Probe.OutputCallbacks);
            _engine.Probe.InputChannels = 1;
            _engine.Probe.BlockFrames = frames;
            return frames * frameBytes;
        }
    }

    /// <summary>
    /// Hands the processed block to the ASIO driver. NAudio calls AudioAvailable first
    /// (which fills _stereo) and then asks this provider for the output data.
    /// </summary>
    private sealed class OutputProvider : IWaveProvider
    {
        private readonly AudioEngine _engine;

        public OutputProvider(WaveFormat format, AudioEngine engine)
        {
            WaveFormat = format;
            _engine = engine;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            int available = _engine._outFrames * 2 * sizeof(float);
            int n = Math.Min(count, available);

            if (n > 0)
                Buffer.BlockCopy(_engine._stereo, 0, buffer, offset, n);
            if (n < count)
                Array.Clear(buffer, offset + n, count - n);

            _engine._outFrames = 0;
            return count;
        }
    }

    public void Dispose() => Stop();
}
