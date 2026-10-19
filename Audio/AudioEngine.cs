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

    public bool IsRunning => _asio != null || _waveOut != null;

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
        catch
        {
            asio.AudioAvailable -= OnAudioAvailable;
            asio.Dispose();
            throw;
        }

        _asio = asio;
    }

    /// <summary>
    /// Runs the pedals from a <see cref="SimulatedGuitar"/> instead of the ASIO interface, and plays the result
    /// through the Windows default output. No guitar, no Guitar Link and no ASIO driver needed.
    /// The extra output latency (about 100 ms) is normal here; this path is for testing sound, not timing.
    /// </summary>
    public void StartSimulated(SimulatedGuitar guitar, int sampleRate)
    {
        Stop();

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
    }

    public void Stop()
    {
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
        int frames = e.SamplesPerBuffer;
        int channels = e.InputBuffers.Length;

        if (channels < 1 || frames > MaxBlock || frames * channels > _interleaved.Length)
        {
            _outFrames = 0;                       // unexpected buffer shape: output silence
            return;
        }

        e.GetAsInterleavedSamples(_interleaved);

        // Take the first (only) input channel.
        for (int i = 0; i < frames; i++)
            _mono[i] = _interleaved[i * channels];

        RunBlock(frames);

        _bufferFrames = frames;
        _outFrames = frames;
    }

    /// <summary>
    /// Shared by the ASIO and simulated paths. <see cref="_mono"/> holds <paramref name="frames"/> raw input
    /// samples; this applies input gain, the pedal chain and output gain, and fills <see cref="_stereo"/>.
    /// </summary>
    private void RunBlock(int frames)
    {
        float inGain = _inputGainLin;
        float inPeak = 0f;
        for (int i = 0; i < frames; i++)
        {
            float s = _mono[i] * inGain;
            _mono[i] = s;
            float a = s < 0f ? -s : s;
            if (a > inPeak) inPeak = a;
        }

        // The pedalboard.
        Chain.Process(_mono, 0, frames);

        // Output gain, safety clamp, mono -> stereo.
        float outGain = _outputGainLin;
        float outPeak = 0f;
        for (int i = 0; i < frames; i++)
        {
            float y = _mono[i] * outGain;
            if (float.IsNaN(y)) y = 0f;
            else if (y > 1f) y = 1f;
            else if (y < -1f) y = -1f;

            _stereo[2 * i] = y;
            _stereo[2 * i + 1] = y;

            float a = y < 0f ? -y : y;
            if (a > outPeak) outPeak = a;
        }

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
