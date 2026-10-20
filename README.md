# jrig

A guitar effects pedalboard for Windows 11. Plug a guitar into a Behringer Guitar Link (UCG102), drag pedals onto the board, and play through your headphones or speakers in real time.

Seventeen pedals so far: **Tuner, Compressor, Overdrive, Distortion, Octave, Wah Wah, Envelope Filter, Chorus, Phaser, Analog Delay, Digital Delay, Looper**, plus five tube amp models: **Marshall Stack, Fender Twin Reverb, Fender Deluxe Reverb, Fender '57 Twin-Amp, Mesa/Boogie**. The app is built so more pedals are easy to add.

### Pedal notes
- **Amps (Marshall Stack, Fender Twin Reverb, Fender Deluxe Reverb, Fender '57 Twin-Amp, Mesa/Boogie):** modelled tube preamp, passive tone stack, sagging power tubes and a speaker cabinet. Knobs read 0 to 10 like the real amps. The **Cab** knob blends the speaker simulation in (100% = full cab, 0% = raw amp, for use with your own cab or IR). Put them after your drive pedals and before modulation and delay. Put a Compressor in front for more sustain on the Fenders. The Fenders add a spring-style Reverb and Bright knob. All five share one engine in `Effects/TubeAmp.cs`, so a new amp is just a new set of numbers.
- **Tuner:** shows the note, cents and a needle. It mutes your sound while it is on (Mute knob at 0 lets the signal through). Switch the pedal off to hear yourself again.
- **Looper:** the footswitch must be ON. REC / PLAY cycles record, play, overdub, play. STOP halts, CLEAR erases. Up to 60 s. The first recording sets the loop length.
- **Wah Wah:** turn *Position* for the heel-to-toe sweep, or raise *Auto* above 0 for an automatic sweep.
- **Octave:** monophonic, play single notes. Chords confuse the octave-down tracking.
- **Analog Delay:** Feedback above 100% self-oscillates on purpose (it stays bounded).
- Time and Mix knobs on the delays and modulation pedals are smoothed, so turning them while playing gives a tape-style pitch swoop rather than clicks.

## What you need

1. **Windows 11** and the **.NET 8 SDK** (https://dotnet.microsoft.com/download).
2. **An ASIO driver** for low latency. The UCG102 usually shows up as a plain USB audio device, so the usual choice is one of:
   - **ASIO4ALL** (open its panel and switch on the Guitar Link), or
   - **FlexASIO** (open source, uses Windows' own audio drivers underneath), or
   - a Behringer ASIO driver, if one is available for your unit.

## Run it

Open a terminal in this folder and run:

    dotnet run -c Release

Or open `Jrig.csproj` in Visual Studio 2022 and press F5.

## Testing without a guitar or interface (simulated guitar)

No Behringer, no guitar, no ASIO driver? Pick a **SOURCE** at the left of the toolbar and press **Start**. A built-in virtual guitar (plucked-string model in standard tuning) feeds the pedals, and you hear the result through your normal Windows speakers or headphones.

| Source | Use it to test |
|---|---|
| Riff (single notes) | any pedal, general feel |
| Scale run | wah, envelope filter, octave tracking across the neck |
| Strummed chords | chorus, phaser, delays, reverb-style tails; shows where octave struggles with chords |
| Dynamics (soft to hard) | compressor, overdrive and distortion response, envelope filter |
| Open strings (tuner test) | tuner: E A D G B e, 3.5 s each |
| Sustained drone (A2) | phaser, chorus, wah sweeps: a steady note makes modulation easy to hear |
| Single note (raw guitar, press Pick) | one open low E that rings and fades like a raw guitar into an amp; the **Pick** button hits it again (re-picking a ringing note adds on top). Good for hearing sustain, compression, delay and envelope behaviour on one clean note |
| Loop an audio file... | a real recording (wav, mp3, flac...). A dry DI guitar track works best |

- Change the source while it is playing (between simulated ones) to switch instantly. Switching to or from the real guitar stops audio first.
- The simulated guitar uses the **Input gain** slider like the real one, so you can push it into a pedal harder.
- Delay through the simulated path is about 100 ms because it uses Windows' standard output rather than ASIO. That is only for testing; the real guitar path is unchanged and low latency.
- Your source choice and audio file are remembered for next launch.

## First-time setup

1. Pick the ASIO driver from the dropdown (click **Refresh** if the list is empty).
2. Leave **Input 1** and **48000 Hz** unless you know you need something else. The Guitar Link is a single mono input.
3. Click **Driver settings** and set the buffer size to **64 to 128 samples**. Smaller means less delay but more risk of crackles. Round-trip delay is roughly twice the buffer time, shown in the status line once audio is running.
4. Press **Start**, play, and watch the INPUT meter. Adjust **Input gain** so loud strums reach the right side of the meter without hitting the end.
5. Keep **Output volume** low to begin with, then raise it.

## Using the board

- **Add a pedal:** drag it from the library onto the board, or double-click it in the library. A blue bar shows where it will land.
- **Reorder:** grab a pedal by its name bar and drag it left or right. Order matters, the signal flows IN to OUT, left to right.
- **Remove:** click the X on the pedal, or drag it back onto the library.
- **Knobs:** drag up/down to turn, hold Shift for fine control, mouse wheel to nudge, double-click to reset.
- **Footswitch:** click the big round button to turn a pedal on or off (red LED = on).
- **Presets:** Save preset / Load preset write your whole board (order, settings, on/off) to a `.json` file. Your last board and audio settings are also restored automatically on the next launch (`%AppData%\jrig\state.json`).

## Adding a new pedal

1. Create a class that derives from `EffectBase` (see `Effects/Compressor.cs`). Add one `Add(...)` line per knob, then write the sound in `ProcessCore`.
2. Add one line to `Effects/EffectCatalog.cs`.

That is all. The library entry, knobs, drag-and-drop, footswitch and preset saving appear automatically. A minimal example:

    public sealed class Boost : EffectBase
    {
        private readonly EffectParameter _drive;

        public Boost() : base("Boost")
        {
            _drive = Add("drive", "Drive", 0f, 40f, 12f, " dB");
        }

        protected override void ProcessCore(float[] buffer, int offset, int count)
        {
            float gain = MathF.Pow(10f, _drive.Value / 20f);
            for (int i = 0; i < count; i++)
                buffer[offset + i] = MathF.Tanh(buffer[offset + i] * gain);
        }
    }

    // EffectCatalog.cs
    new CatalogEntry("Boost", "Clean-ish drive boost.", "#E4572E", () => new Boost()),

Pedals that want a live readout or buttons (tuner, looper) also implement `IPedalReadout` / `IPedalActions` (see `IEffect.cs`); the pedal UI picks them up automatically. Shared DSP building blocks (biquad, state-variable filter, delay line, LFO, envelope follower) live in `Effects/Dsp.cs`.

Any file that uses `File`, `Path`, `Directory` or `InvalidDataException` needs `using System.IO;` at the top. Do not rely on implicit usings for it.

Rules for `ProcessCore`: it runs on the audio thread, so no allocating memory, no locks, no file access, no UI calls.

## How it is put together

    Audio/AudioEngine.cs      ASIO in (or the simulated guitar) -> input gain -> pedal chain -> output gain -> out (mono to both ears)
    Audio/SimulatedGuitar.cs  built-in virtual guitar for testing: Karplus-Strong strings, note sequences, audio-file loop
    Audio/EffectChain.cs      the ordered list of pedals; edits are swapped in safely while audio runs
    Effects/                  IEffect, EffectBase (bypass crossfade), Dsp.cs (shared filters, delay line, LFO), the 12 pedals + TubeAmp (5 amp models), EffectCatalog
    Controls/                 KnobControl (drawn knob) and PedalControl (the pedal on the board)
    ViewModels/               glue between the effects and the UI
    Persistence/StateStore.cs presets and session restore (JSON)
    MainWindow.xaml(.cs)      toolbar, meters, pedal library, pedalboard, drag-and-drop

## Troubleshooting

- **Driver list is empty:** install ASIO4ALL or FlexASIO and click Refresh. If it is installed but still not listed, it may be a 32-bit driver and this app is 64-bit. Reinstalling the latest ASIO4ALL (which registers both) usually fixes it.
- **"Could not start audio":** close other programs that may be using the interface (DAWs, browsers playing audio), try the other sample rate, and check the Guitar Link is switched on in the driver settings.
- **Crackles or dropouts:** raise the buffer size in Driver settings (try 128, then 256).
- **Too much delay:** lower the buffer size in Driver settings.
- **Very quiet or hissy:** raise Input gain a little. Make sure the Guitar Link's own input level is up.

## Windows audio (recommended for the Guitar Link) and Diagnostics

The toolbar **AUDIO** box now offers **Windows audio** (the same WASAPI path Deplike and AmpliTube use) or **ASIO**.
For the Guitar Link choose Windows audio, **IN** = *Microphone (USB Audio CODEC)*, **OUT** = *Speakers (USB Audio CODEC)*, keep **Exclusive** ticked (untick it if another program has the device).

If you hear nothing, press **Diagnostics**:
- A verdict at the top says which stage the signal dies at (device stream, device input, input gain, a specific pedal, output volume, output device).
- The stage list shows live levels from the device input through every pedal to the output.
- **Test tone** proves the output path; **Bypass pedals** proves the pedals are not the cause.
- **Copy full report** copies the verdict, a full device list and the log.
- The log is written to `%AppData%\jrig\logs\jrig-<date>.log` on every run (last 10 kept).
