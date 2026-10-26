using Jrig.Effects;

namespace Jrig.Audio;

/// <summary>One pedal on the board plus the level meters the diagnostics window reads.</summary>
public sealed class ChainSlot
{
    public ChainSlot(IEffect effect) { Effect = effect; }
    public IEffect Effect { get; }
    public float InPeak;     // highest sample going into the pedal since the diagnostics window last looked
    public float OutPeak;    // highest sample coming out
}

/// <summary>
/// The ordered list of pedals the signal flows through.
///
/// The UI thread edits the chain (add / remove / reorder); the audio thread only reads it.
/// Every edit builds a brand-new array and publishes it with a single reference write,
/// so the audio thread never sees a half-edited list and never needs a lock.
/// </summary>
public sealed class EffectChain
{
    private readonly object _editLock = new();     // serialises UI-thread edits only
    private ChainSlot[] _live = Array.Empty<ChainSlot>();
    private int _sampleRate = 48000;
    private int _maxBlock = AudioEngine.MaxBlock;

    private volatile bool _bypassAll;

    /// <summary>True = the whole chain is skipped (pure guitar). Every pedal keeps its settings and on/off state.</summary>
    public bool BypassAll { get => _bypassAll; set => _bypassAll = value; }

    public IReadOnlyList<IEffect> Effects => Volatile.Read(ref _live).Select(s => s.Effect).ToArray();

    /// <summary>Current pedals with their level probes (diagnostics window).</summary>
    public IReadOnlyList<ChainSlot> Slots => Volatile.Read(ref _live);

    /// <summary>Call while audio is stopped (the engine does this in Start).</summary>
    public void Prepare(int sampleRate, int maxBlock)
    {
        lock (_editLock)
        {
            _sampleRate = sampleRate;
            _maxBlock = maxBlock;
            foreach (var slot in _live)
                slot.Effect.Prepare(sampleRate, maxBlock);
        }
    }

    public void Insert(int index, IEffect effect)
    {
        lock (_editLock)
        {
            effect.Prepare(_sampleRate, _maxBlock);        // not live yet, so this is safe
            var list = new List<ChainSlot>(_live);
            list.Insert(Math.Clamp(index, 0, list.Count), new ChainSlot(effect));
            Volatile.Write(ref _live, list.ToArray());
        }
    }

    public void Remove(IEffect effect)
    {
        lock (_editLock)
        {
            var list = new List<ChainSlot>(_live);
            int i = list.FindIndex(s => ReferenceEquals(s.Effect, effect));
            if (i >= 0)
            {
                list.RemoveAt(i);
                Volatile.Write(ref _live, list.ToArray());
            }
        }
    }

    /// <summary>Moves the pedal at <paramref name="from"/> so it ends up at index <paramref name="to"/>.</summary>
    public void Move(int from, int to)
    {
        lock (_editLock)
        {
            var list = new List<ChainSlot>(_live);
            if (from < 0 || from >= list.Count) return;
            var slot = list[from];
            list.RemoveAt(from);
            list.Insert(Math.Clamp(to, 0, list.Count), slot);
            Volatile.Write(ref _live, list.ToArray());
        }
    }

    /// <summary>Audio thread. Runs the block through every pedal in order, noting levels in and out of each.</summary>
    public void Process(float[] buffer, int offset, int count)
    {
        if (_bypassAll) return;
        var live = Volatile.Read(ref _live);
        for (int i = 0; i < live.Length; i++)
        {
            var slot = live[i];
            SignalProbe.Max(ref slot.InPeak, Peak(buffer, offset, count));
            slot.Effect.Process(buffer, offset, count);
            SignalProbe.Max(ref slot.OutPeak, Peak(buffer, offset, count));
        }
    }

    internal static float Peak(float[] buffer, int offset, int count)
    {
        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float a = buffer[offset + i];
            if (a < 0f) a = -a;
            if (a > peak) peak = a;          // NaN compares false, so it is skipped here and counted later
        }
        return peak;
    }
}
