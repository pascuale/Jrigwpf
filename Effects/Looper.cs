using System;
using System.Threading;

namespace Jrig.Effects;

public enum LooperState { Empty = 0, Recording = 1, Playing = 2, Overdubbing = 3, Stopped = 4 }

/// <summary>
/// Mono looper with several loop slots (SlotCount, each up to MaxSeconds). The pedal's buttons drive it:
///   Slot buttons 1..N  an empty slot starts recording into it, a slot holding a loop switches to it and plays it,
///                      pressing the slot that is already selected toggles stop / play (or ends a recording).
///   REC/PLAY           acts on the selected slot: Empty -> Recording -> Playing -> Overdubbing -> Playing ...
///                      (and Stopped -> Playing)
///   STOP               halts playback of the selected slot, CLEAR erases the selected slot only.
/// Commands are queued from the UI and applied at the start of the next audio block.
/// The first recording in a slot fixes that slot's loop length. Only one slot sounds at a time.
/// The pedal's footswitch must be ON for the looper to run: switching it off bypasses the pedal and
/// pauses the loop where it is. Loops live in memory for the session (they are not written to disk).
/// </summary>
public sealed class Looper : EffectBase, IPedalReadout, IPedalActions, IPedalSlots, IExemptFromBypassAll
{
    public const int MaxSeconds = 30;      // per slot; SlotCount x MaxSeconds x 4 bytes x sample rate of memory in total
    public const int SlotCount = 8;

    private const int CmdNone = 0, CmdFootswitch = 1, CmdStop = 2, CmdClear = 3, CmdSlotBase = 100;

    private readonly EffectParameter _level;
    private readonly EffectParameter _decay;
    private readonly EffectParameter _dry;

    private readonly float[][] _slotBuf = new float[SlotCount][];
    private readonly int[] _slotLen = new int[SlotCount];          // samples recorded per slot (0 = empty); audio thread
    private int _cur;                    // selected slot (audio thread)
    private float[] _loop;               // buffer of the selected slot
    private int _length;                 // samples in the selected loop (0 = nothing recorded)
    private int _pos;
    private LooperState _state = LooperState.Empty;     // state of the selected slot
    private Smooth _overdubGain;         // click-free fade of the overdub gate
    private Smooth _playGain;            // short fade-in whenever a loop starts, so slot switches do not click

    private int _pending;                // UI -> audio thread command
    private volatile int _stateOut;      // audio thread -> UI
    private volatile int _curOut;
    private volatile float _progressOut;
    private volatile float _secondsOut;
    private readonly int[] _slotStateOut = new int[SlotCount];
    private readonly float[] _slotSecondsOut = new float[SlotCount];

    public Looper() : base("Looper")
    {
        _level = Add("level", "Loop Level", 0f, 150f, 100f, " %", ParameterCurve.Linear, 0);
        _decay = Add("decay", "Overdub Fade", 50f, 100f, 100f, " %", ParameterCurve.Linear, 0);   // 100 = old layers never fade
        _dry = Add("dry", "Dry", 0f, 100f, 100f, " %", ParameterCurve.Linear, 0);

        for (int i = 0; i < SlotCount; i++) _slotBuf[i] = new float[1];
        _loop = _slotBuf[0];

        Actions = new[]
        {
            new PedalAction("REC / PLAY", () => Volatile.Write(ref _pending, CmdFootswitch)),
            new PedalAction("STOP", () => Volatile.Write(ref _pending, CmdStop)),
            new PedalAction("CLEAR", () => Volatile.Write(ref _pending, CmdClear)),
        };
    }

    public IReadOnlyList<PedalAction> Actions { get; }
    public LooperState State => (LooperState)_stateOut;

    // ---- IPedalSlots (UI thread) ----
    int IPedalSlots.SlotCount => SlotCount;
    public int SelectedSlot => _curOut;
    public void PressSlot(int index)
    {
        if (index >= 0 && index < SlotCount) Volatile.Write(ref _pending, CmdSlotBase + index);
    }
    public int SlotState(int index) => (uint)index < SlotCount ? Volatile.Read(ref _slotStateOut[index]) : 0;
    public float SlotSeconds(int index) => (uint)index < SlotCount ? Volatile.Read(ref _slotSecondsOut[index]) : 0f;

    public string Headline => State switch
    {
        LooperState.Empty => "EMPTY",
        LooperState.Recording => "RECORDING",
        LooperState.Playing => "PLAYING",
        LooperState.Overdubbing => "OVERDUB",
        _ => "STOPPED",
    };

    public string Detail => _secondsOut > 0f
        ? $"loop {_curOut + 1}  {_secondsOut:F1} s"
        : $"loop {_curOut + 1}  press REC / PLAY";
    public bool HasNeedle => false;
    public float Needle => 0f;
    public bool HasProgress => true;
    public float Progress => State == LooperState.Recording ? _secondsOut / MaxSeconds : _progressOut;

    protected override void OnPrepare()
    {
        // Prepare runs again on every Start. Keep the recorded loops unless the buffer size has to change.
        int needed = MaxSeconds * SampleRate;
        if (_slotBuf[0].Length != needed)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                _slotBuf[i] = new float[needed];
                _slotLen[i] = 0;
            }
            _cur = 0;
            _loop = _slotBuf[0];
            _length = 0;
            _pos = 0;
            _state = LooperState.Empty;
        }
        _overdubGain.Init(0f, 8f, SampleRate);
        _playGain.Init(1f, 4f, SampleRate);
        Publish();
    }

    private void Apply(int cmd)
    {
        if (cmd >= CmdSlotBase)
        {
            ApplySlot(cmd - CmdSlotBase);
            return;
        }

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
                    case LooperState.Stopped: _pos = 0; _state = LooperState.Playing; _playGain.Set(0f); break;
                }
                break;

            case CmdStop:
                if (_state == LooperState.Recording && _length == 0) _state = LooperState.Empty;
                else if (_state != LooperState.Empty) { _pos = 0; _state = LooperState.Stopped; }
                break;

            case CmdClear:
                // Only the length is reset: a new recording overwrites the old samples, so no big clear is needed here.
                _length = 0; _pos = 0; _state = LooperState.Empty;
                break;
        }
    }

    /// <summary>A slot button was pressed.</summary>
    private void ApplySlot(int slot)
    {
        if (slot < 0 || slot >= SlotCount) return;

        if (slot == _cur)
        {
            // Same slot: the button is a transport toggle.
            switch (_state)
            {
                case LooperState.Empty:
                    _pos = 0; _length = 0; _state = LooperState.Recording; break;
                case LooperState.Recording:
                    if (_length > 0) { _pos = 0; _state = LooperState.Playing; _playGain.Set(0f); }
                    else _state = LooperState.Empty;
                    break;
                case LooperState.Playing:
                case LooperState.Overdubbing:
                    _pos = 0; _state = LooperState.Stopped; break;
                case LooperState.Stopped:
                    _pos = 0; _state = LooperState.Playing; _playGain.Set(0f); break;
            }
            return;
        }

        // Leave the current slot. A recording in progress is kept as it is (shorter loop); an empty one is dropped.
        if (_state == LooperState.Recording && _length == 0) _state = LooperState.Empty;
        _slotLen[_cur] = _length;

        _cur = slot;
        _loop = _slotBuf[slot];
        _length = _slotLen[slot];
        _pos = 0;
        _overdubGain.Init(0f, 8f, SampleRate);

        if (_length > 0)
        {
            _state = LooperState.Playing;           // a slot holding a loop plays it from the start
            _playGain.Set(0f);
        }
        else
        {
            _state = LooperState.Recording;         // an empty slot starts recording straight away
            _playGain.Set(1f);
        }
    }

    private void Publish()
    {
        _slotLen[_cur] = _length;
        _stateOut = (int)_state;
        _curOut = _cur;
        float sr = SampleRate > 0 ? SampleRate : 1f;
        _secondsOut = SampleRate > 0 ? (float)(_state == LooperState.Recording ? _pos : _length) / sr : 0f;
        _progressOut = _length > 0 ? (float)_pos / _length : 0f;

        for (int i = 0; i < SlotCount; i++)
        {
            int len = _slotLen[i];
            Volatile.Write(ref _slotStateOut[i], i == _cur ? (int)_state : (len > 0 ? (int)LooperState.Stopped : (int)LooperState.Empty));
            Volatile.Write(ref _slotSecondsOut[i], len / sr);
        }
    }

    /// <summary>Set by the chain for one block when a drum machine sits after the looper on the board: the drums are
    /// recorded into the loop with the guitar, but are not added to the looper's dry output (the drum pedal adds them later).</summary>
    public float[]? ExtraInput;

    protected override void ProcessCore(float[] buffer, int offset, int count)
    {
        float[]? extra = ExtraInput;
        int cmd = Interlocked.Exchange(ref _pending, CmdNone);
        if (cmd != CmdNone) Apply(cmd);

        float level = _level.Value * 0.01f;
        float decay = _decay.Value * 0.01f;
        float dry = _dry.Value * 0.01f;
        bool overdubbing = _state == LooperState.Overdubbing;

        for (int i = 0; i < count; i++)
        {
            float x = buffer[offset + i];
            float rec = extra != null ? x + extra[i] : x;      // what goes into the loop: guitar plus any later drums
            float wet = 0f;

            if (_state == LooperState.Recording)
            {
                if (_pos < _loop.Length)
                {
                    _loop[_pos++] = rec;
                    _length = _pos;
                }
                else
                {
                    _pos = 0;                          // ran out of room: close the loop automatically
                    _state = LooperState.Playing;
                    _playGain.Set(0f);
                }
            }
            else if ((_state == LooperState.Playing || _state == LooperState.Overdubbing) && _length > 0)
            {
                float g = _overdubGain.Next(overdubbing ? 1f : 0f);
                float old = _loop[_pos];
                wet = old * level * _playGain.Next(1f);
                if (g > 0.001f)
                {
                    // Sum the new playing into the loop; the limiter keeps stacked layers from clipping.
                    float keep = 1f + (decay - 1f) * g;
                    _loop[_pos] = Dsp.SoftLimit(old * keep + rec * g);
                }
                if (++_pos >= _length) _pos = 0;
            }

            buffer[offset + i] = x * dry + wet;
        }

        Publish();
    }
}
