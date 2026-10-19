namespace Jrig.Effects;

/// <summary>
/// Feed-forward compressor with a soft knee.
///
/// Signal path: peak level detector (dB) -> gain computer with soft knee ->
/// attack/release smoothing of the gain reduction (in dB) -> gain applied,
/// plus makeup gain and a dry/wet mix (mix below 100% = parallel compression).
/// </summary>
public sealed class Compressor : EffectBase, IGainReductionMeter
{
    private const float DbToNeper = 0.115129255f;   // ln(10) / 20

    private readonly EffectParameter _threshold;
    private readonly EffectParameter _ratio;
    private readonly EffectParameter _attack;
    private readonly EffectParameter _release;
    private readonly EffectParameter _knee;
    private readonly EffectParameter _makeup;
    private readonly EffectParameter _mix;

    private readonly Smoother _makeupSmoother = new();
    private readonly Smoother _mixSmoother = new();

    private float _envDb;                 // smoothed gain reduction in dB (>= 0)
    private volatile float _meterDb;      // for the UI

    private float _cachedAttackMs = -1f;
    private float _cachedReleaseMs = -1f;
    private float _attackCoeff;
    private float _releaseCoeff;

    public Compressor() : base("Compressor")
    {
        _threshold = Add("threshold", "Threshold", -60f, 0f, -20f, " dB", ParameterCurve.Linear, 1);
        _ratio = Add("ratio", "Ratio", 1f, 20f, 4f, ":1", ParameterCurve.Logarithmic, 1);
        _attack = Add("attack", "Attack", 0.1f, 100f, 10f, " ms", ParameterCurve.Logarithmic, 1);
        _release = Add("release", "Release", 10f, 1000f, 120f, " ms", ParameterCurve.Logarithmic, 0);
        _knee = Add("knee", "Knee", 0f, 24f, 6f, " dB", ParameterCurve.Linear, 1);
        _makeup = Add("makeup", "Makeup", 0f, 24f, 0f, " dB", ParameterCurve.Linear, 1);
        _mix = Add("mix", "Mix", 0f, 100f, 100f, " %", ParameterCurve.Linear, 0);
    }

    public float GainReductionDb => _meterDb;

    protected override void OnPrepare()
    {
        _makeupSmoother.Configure(SampleRate, 20f);
        _mixSmoother.Configure(SampleRate, 20f);
        _cachedAttackMs = -1f;      // force coefficient refresh
        _cachedReleaseMs = -1f;
        _envDb = 0f;
        _meterDb = 0f;
    }

    protected override void ProcessCore(float[] buffer, int offset, int count)
    {
        float thresholdDb = _threshold.Value;
        float ratio = _ratio.Value;
        float kneeDb = _knee.Value;
        float makeupTarget = _makeup.Value;
        float mixTarget = _mix.Value * 0.01f;

        UpdateCoefficients();

        float slope = 1f - 1f / ratio;      // 0 at 1:1, approaches 1 as ratio -> infinity
        float halfKnee = kneeDb * 0.5f;
        float peakGr = 0f;

        for (int i = 0; i < count; i++)
        {
            float x = buffer[offset + i];

            // 1. Level detector (instantaneous peak, in dB). The tiny offset avoids log10(0).
            float levelDb = 20f * MathF.Log10(MathF.Abs(x) + 1e-9f);

            // 2. Gain computer: how many dB we want to turn the signal down by.
            float over = levelDb - thresholdDb;
            float targetGr;
            if (over <= -halfKnee)
            {
                targetGr = 0f;                                   // below the knee
            }
            else if (over >= halfKnee)
            {
                targetGr = slope * over;                         // above the knee: full ratio
            }
            else
            {
                float t = over + halfKnee;                       // inside the knee: quadratic blend
                targetGr = slope * t * t / (2f * kneeDb);
            }

            // 3. Attack/release smoothing of the gain reduction (branching detector, in dB).
            float coeff = targetGr > _envDb ? _attackCoeff : _releaseCoeff;
            _envDb = coeff * _envDb + (1f - coeff) * targetGr;
            if (_envDb < 1e-5f) _envDb = 0f;                     // avoid denormals

            if (_envDb > peakGr) peakGr = _envDb;

            // 4. Apply gain (+ smoothed makeup) and the dry/wet mix.
            float makeupDb = _makeupSmoother.Next(makeupTarget);
            float gain = MathF.Exp((makeupDb - _envDb) * DbToNeper);
            float wet = x * gain;
            float mix = _mixSmoother.Next(mixTarget);
            buffer[offset + i] = x + (wet - x) * mix;
        }

        // Meter: instant rise, ~150 ms fall, so the UI bar is readable.
        float fall = MathF.Exp(-count / (SampleRate * 0.15f));
        float meter = _meterDb * fall;
        _meterDb = peakGr > meter ? peakGr : meter;
    }

    private void UpdateCoefficients()
    {
        float attackMs = _attack.Value;
        if (attackMs != _cachedAttackMs)
        {
            _cachedAttackMs = attackMs;
            _attackCoeff = MathF.Exp(-1f / (0.001f * attackMs * SampleRate));
        }

        float releaseMs = _release.Value;
        if (releaseMs != _cachedReleaseMs)
        {
            _cachedReleaseMs = releaseMs;
            _releaseCoeff = MathF.Exp(-1f / (0.001f * releaseMs * SampleRate));
        }
    }
}
