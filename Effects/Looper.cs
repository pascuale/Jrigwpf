using System;
using System.Threading;

namespace Jrig.Effects;

public enum LooperState { Empty = 0, Recording = 1, Playing = 2, Overdubbing = 3, Stopped = 4 }

/// <summary>
/// Single-track mono looper, up to 60 s. The pedal's buttons drive it:
///   REC/PLAY cycles Empty -> Recording -> Playing -> Overdubbing -> Playing ...  (and Stopped -> Playing)
///   STOP halts playback, CLEAR erases the loop.
/// Commands are queued from the UI and applied at the start of the next audio block.
/// The first recording fixes the loop length. The pedal's footswitch must be ON for the looper to run:
/// switching it off bypasses the pedal and pauses the loop where it is.
/// </summary>
public sealed class Looper : EffectBase, IPedalReadout, IPedalActions
{
    public const int MaxSeconds = 60;

    private const int CmdNone = 0, CmdFootswitch = 1, CmdStop = 2, CmdClear = 3;

    private readonly EffectParameter _level;
    private readonly EffectParameter _decay;
    private readonly EffectParameter _dry;

    private float[] _loop = new float[1];
    private int _length;                 // samples in the loop (0 = nothing recorded)
    private int _pos;
    private LooperState _state = LooperState.Empty;
    private Smooth _overdubGain;         // click-free fade of the overdub gate

    private int _pending;                // UI -> audio thread command
    private volatile int _stateOut;      // audio thread -> UI
    private volatile float _progressOut;
    private volatile float _secondsOut;

    public Looper() : base("Looper")
    {
        _level = Add("level", "Loop Level", 0f, 150f, 100f, " %", ParameterCurve.Linear, 0);
        _decay = Add("decay", "Overdub Fade", 50f, 100f, 100f, " %", ParameterCurve.Linear, 0);   // 100 = old layers never fade
        _dry = Add("dry", "Dry", 0f, 100f, 100f, " %", ParameterCurve.Linear, 0);

        Actions = new[]
        {
            new PedalAction("REC / PLAY", () => Volatile.Write(ref _pending, CmdFootswitch)),
            new PedalAction("STOP", () => Volatile.Write(ref _pending, CmdStop)),
            new PedalAction("CLEAR", () => Volatile.Write(ref _pending, CmdClear)),
        };
    }

    public IReadOnlyList<PedalAction> Actions { get; }
    public LooperState State => (LooperState)_stateOut;

    public string Headline => State switch
    {
        LooperState.Empty => "EMPTY",
        LooperState.Recording => "RECORDING",
        LooperState.Playing => "PLAYING",
        LooperState.Overdubbing => "OVERDUB",
        _ => "STOPPED",
    };

    public string Detail => _secondsOut > 0f ? $"loop {_secondsOut:F1} s" : "press REC / PLAY";
    public bool HasNeedle => false;
    public float Needle => 0f;
    public bool HasProgress => true;
    public float Progress => State == LooperState.Recording ? _secondsOut / MaxSeconds : _progressOut;

    protected override void OnPrepare()
    {
        // Prepare runs again on every Start. Keep the recorded loop unless the buffer size has to change.
        int needed = MaxSeconds * SampleRate;
        if (_loop.Length != needed)
        {
            _loop = new float[needed];
            _length = 0;
            _pos = 0;
            _state = LooperState.Empty;
        }
        _overdubGain.Init(0f, 8f, SampleRate);
        Publish();
    }

    private void Apply(int cmd)
    {
        switch (cmd)
        {
            case CmdFootswitch:
                switch (_state)
                {
                    case LooperState.Empty:
                        _pos = 0; _length = 0; _state = LooperState.Recording; break;
                    case LooperState.Recording:
                        if (_length > 0) { _pos = 0; _state = LooperState.Playing; }
                        else _state = LooperState.Empty;
                        break;
                    case LooperState.Playing: _state = LooperState.Overdubbing; break;
                    case LooperState.Overdubbing: _state = LooperState.Playing; break;
                    case LooperState.Stopped: _pos = 0; _state = LooperState.Playing; break;
                }
                break;

            case CmdStop:
                if (_state == LooperState.Recording && _length == 0) _state = LooperState.Empty;
                else if (_state != LooperState.Empty) { _pos = 0; _state = LooperState.Stopped; }
                break;

            case CmdClear:
                Array.Clear(_loop, 0, _loop.Length);
                _length = 0; _pos = 0; _state = LooperState.Empty;
                break;
        }
    }

    private void Publish()
    {
        _stateOut = (int)_state;
        _secondsOut = SampleRate > 0 ? (float)(_state == LooperState.Recording ? _pos : _length) / SampleRate : 0f;
        _progressOut = _length > 0 ? (float)_pos / _length : 0f;
    }

    protected override void ProcessCore(float[] buffer, int offset, int count)
    {
        int cmd = Interlocked.Exchange(ref _pending, CmdNone);
        if (cmd != CmdNone) Apply(cmd);

        float level = _level.Value * 0.01f;
        float decay = _decay.Value * 0.01f;
        float dry = _dry.Value * 0.01f;
        bool overdubbing = _state == LooperState.Overdubbing;

        for (int i = 0; i < count; i++)
        {
            float x = buffer[offset + i];
            float wet = 0f;

            if (_state == LooperState.Recording)
            {
                if (_pos < _loop.Length)
                {
                    _loop[_pos++] = x;
                    _length = _pos;
                }
                else
                {
                    _pos = 0;                          // ran out of room: close the loop automatically
                    _state = LooperState.Playing;
                }
            }
            else if ((_state == LooperState.Playing || _state == LooperState.Overdubbing) && _length > 0)
            {
                float g = _overdubGain.Next(overdubbing ? 1f : 0f);
                float old = _loop[_pos];
                wet = old * level;
                if (g > 0.001f)
                {
                    // Sum the new playing into the loop; the limiter keeps stacked layers from clipping.
                    float keep = 1f + (decay - 1f) * g;
                    _loop[_pos] = Dsp.SoftLimit(old * keep + x * g);
                }
                if (++_pos >= _length) _pos = 0;
            }

            buffer[offset + i] = x * dry + wet;
        }

        Publish();
    }
}
