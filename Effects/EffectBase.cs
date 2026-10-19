namespace Jrig.Effects;

/// <summary>
/// Shared plumbing for pedals: parameter list, bypass flag, and a short
/// crossfade when bypass is toggled so the footswitch does not click.
/// Derive from this, call <see cref="Add"/> for each knob, and implement ProcessCore.
/// </summary>
public abstract class EffectBase : IEffect
{
    private readonly List<EffectParameter> _parameters = new();
    private volatile bool _bypassed;
    private float[] _dry = Array.Empty<float>();
    private float _bypassMix = 1f;     // 1 = fully effected, 0 = fully bypassed (audio thread only)
    private float _bypassStep = 0.01f;

    protected EffectBase(string typeName)
    {
        TypeName = typeName;
    }

    public string TypeName { get; }

    public bool Bypassed
    {
        get => _bypassed;
        set => _bypassed = value;
    }

    public IReadOnlyList<EffectParameter> Parameters => _parameters;

    protected int SampleRate { get; private set; } = 48000;

    protected EffectParameter Add(string id, string name, float min, float max, float defaultValue,
        string unit = "", ParameterCurve curve = ParameterCurve.Linear, int decimals = 1)
    {
        var p = new EffectParameter(id, name, min, max, defaultValue, unit, curve, decimals);
        _parameters.Add(p);
        return p;
    }

    public void Prepare(int sampleRate, int maxBlockSize)
    {
        SampleRate = sampleRate;
        _dry = new float[maxBlockSize];
        _bypassStep = 1f / MathF.Max(1f, sampleRate * 0.005f);   // ~5 ms crossfade
        _bypassMix = _bypassed ? 0f : 1f;
        OnPrepare();
    }

    public void Process(float[] buffer, int offset, int count)
    {
        bool active = !_bypassed;

        if (!active && _bypassMix <= 0f)
            return;                                   // fully bypassed: nothing to do

        if ((active && _bypassMix >= 1f) || count > _dry.Length)
        {
            ProcessCore(buffer, offset, count);       // fully active
            return;
        }

        // Transition in progress: keep a copy of the dry signal and crossfade.
        Array.Copy(buffer, offset, _dry, 0, count);
        ProcessCore(buffer, offset, count);

        for (int i = 0; i < count; i++)
        {
            _bypassMix = active
                ? MathF.Min(1f, _bypassMix + _bypassStep)
                : MathF.Max(0f, _bypassMix - _bypassStep);

            float dry = _dry[i];
            buffer[offset + i] = dry + (buffer[offset + i] - dry) * _bypassMix;
        }
    }

    /// <summary>Allocate buffers / compute coefficients that depend on the sample rate.</summary>
    protected virtual void OnPrepare() { }

    /// <summary>The actual DSP. Mono, in place. Audio thread: keep it allocation-free.</summary>
    protected abstract void ProcessCore(float[] buffer, int offset, int count);
}
