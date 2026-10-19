namespace Jrig.Effects;

/// <summary>One-pole parameter smoother, prevents zipper noise when a knob is turned.</summary>
internal sealed class Smoother
{
    private float _current;
    private float _coeff = 0.99f;
    private bool _primed;

    public void Configure(int sampleRate, float timeMs)
    {
        _coeff = MathF.Exp(-1f / (0.001f * timeMs * sampleRate));
        _primed = false;
    }

    public float Next(float target)
    {
        if (!_primed)
        {
            _current = target;
            _primed = true;
        }
        else
        {
            _current = target + _coeff * (_current - target);
        }
        return _current;
    }
}
