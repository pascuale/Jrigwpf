using Jrig.Effects;

namespace Jrig.Audio;

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
    private IEffect[] _live = Array.Empty<IEffect>();
    private int _sampleRate = 48000;
    private int _maxBlock = AudioEngine.MaxBlock;

    public IReadOnlyList<IEffect> Effects => Volatile.Read(ref _live);

    /// <summary>Call while audio is stopped (the engine does this in Start).</summary>
    public void Prepare(int sampleRate, int maxBlock)
    {
        lock (_editLock)
        {
            _sampleRate = sampleRate;
            _maxBlock = maxBlock;
            foreach (var fx in _live)
                fx.Prepare(sampleRate, maxBlock);
        }
    }

    public void Insert(int index, IEffect effect)
    {
        lock (_editLock)
        {
            effect.Prepare(_sampleRate, _maxBlock);        // not live yet, so this is safe
            var list = new List<IEffect>(_live);
            list.Insert(Math.Clamp(index, 0, list.Count), effect);
            Volatile.Write(ref _live, list.ToArray());
        }
    }

    public void Remove(IEffect effect)
    {
        lock (_editLock)
        {
            var list = new List<IEffect>(_live);
            if (list.Remove(effect))
                Volatile.Write(ref _live, list.ToArray());
        }
    }

    /// <summary>Moves the pedal at <paramref name="from"/> so it ends up at index <paramref name="to"/>.</summary>
    public void Move(int from, int to)
    {
        lock (_editLock)
        {
            var list = new List<IEffect>(_live);
            if (from < 0 || from >= list.Count) return;
            var fx = list[from];
            list.RemoveAt(from);
            list.Insert(Math.Clamp(to, 0, list.Count), fx);
            Volatile.Write(ref _live, list.ToArray());
        }
    }

    /// <summary>Audio thread. Runs the block through every pedal in order.</summary>
    public void Process(float[] buffer, int offset, int count)
    {
        var live = Volatile.Read(ref _live);
        for (int i = 0; i < live.Length; i++)
            live[i].Process(buffer, offset, count);
    }
}
