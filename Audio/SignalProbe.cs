namespace Jrig.Audio;

/// <summary>
/// Lock-free counters and peak meters written by the audio threads and read by the diagnostics window.
/// Peaks are "highest since last Take", so the reader resets them. Only one reader (the diagnostics
/// window) calls Take*, separate from the main window meters.
/// </summary>
public sealed class SignalProbe
{
    public const int MaxChannels = 8;

    // --- peaks (written by audio thread via Max, cleared by reader via Take)
    public readonly float[] RawChannelPeak = new float[MaxChannels];   // every channel the device delivered
    public float SelectedPeak;     // chosen input channel, before gain
    public float AfterGainPeak;    // after input gain
    public float AfterChainPeak;   // after all pedals
    public float FinalPeak;        // after output volume + clamp (what is handed to the output device)

    // --- counters
    public long InputCallbacks, OutputCallbacks;
    public long InputFrames, OutputFrames;
    public long ZeroInputBlocks;       // blocks where every raw sample was exactly 0.0
    public long NonFiniteSamples;      // NaN / infinity found after the chain
    public long Underruns, Overruns;   // WASAPI ring buffer
    public long LastInputTicks, LastOutputTicks;   // Stopwatch timestamps

    // --- facts (set when audio starts)
    public volatile string Backend = "";
    public volatile string InputFormat = "";
    public volatile string OutputFormat = "";
    public volatile int InputChannels;
    public volatile int BlockFrames;

    public static void Max(ref float field, float value)
    {
        if (value > field) field = value;
    }

    public static float Take(ref float field) => Interlocked.Exchange(ref field, 0f);

    public void ResetAll()
    {
        for (int i = 0; i < MaxChannels; i++) RawChannelPeak[i] = 0f;
        SelectedPeak = AfterGainPeak = AfterChainPeak = FinalPeak = 0f;
        InputCallbacks = OutputCallbacks = InputFrames = OutputFrames = 0;
        ZeroInputBlocks = NonFiniteSamples = Underruns = Overruns = 0;
        LastInputTicks = LastOutputTicks = 0;
        InputChannels = 0;
        BlockFrames = 0;
        InputFormat = OutputFormat = "";
    }
}
