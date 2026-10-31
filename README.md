# jrig

A guitar effects pedalboard for Windows 11. Plug a guitar into a Behringer Guitar Link (UCG102), drag pedals onto the board, and play through your headphones or speakers in real time.

Twenty pedals so far: **Tuner, Compressor, Overdrive, Distortion, Octave, Wah Wah, Envelope Filter, Chorus, Phaser, Analog Delay, Digital Delay, Drum Machine, Looper, Wind Synth, Bass Synth**, plus five tube amp models: **Marshall Stack, Fender Twin Reverb, Fender Deluxe Reverb, Fender '57 Twin-Amp, Mesa/Boogie**. The app is built so more pedals are easy to add.

### Pedal notes
- **Amps (Marshall Stack, Fender Twin Reverb, Fender Deluxe Reverb, Fender '57 Twin-Amp, Mesa/Boogie):** modelled tube preamp, passive tone stack, sagging power tubes and a speaker cabinet. Knobs read 0 to 10 like the real amps. The **Cab** knob blends the speaker simulation in (100% = full cab, 0% = raw amp, for use with your own cab or IR). Put them after your drive pedals and before modulation and delay. Put a Compressor in front for more sustain on the Fenders. The Fenders add a spring-style Reverb and Bright knob. All five share one engine in `Effects/TubeAmp.cs`, so a new amp is just a new set of numbers.
- **Tuner:** shows the note, cents and a needle. It mutes your sound while it is on (Mute knob at 0 lets the signal through). Switch the pedal off to hear yourself again.
- **Drum Machine:** synthesised drums that play along with your guitar (your guitar is not changed). 12 styles: Metronome, Rock, Pop, Disco, Funk, Shuffle, Slow Blues, Half-Time, Metal, Reggae, Bossa Nova, Hip-Hop. Turn the Style knob to pick one (its name shows under the knob). Swing pushes the off-beat 16ths late, Tone brightens the kit, Decay makes it tighter or boomier. Switch the pedal on and the drums start on beat 1. RESTART jumps back to beat 1, FILL plays a tom fill in the second half of the bar.
- **Looper:** the footswitch must be ON. Eight loop slots (buttons 1 to 8, up to 30 s each, kept in memory while the app runs). Press an empty slot to record into it, press again to start playing. Press a slot that holds a loop to switch to it; press the selected slot to stop or restart it. REC / PLAY, STOP and CLEAR act on the selected slot (white outline): REC / PLAY cycles record, play, overdub, play. Colours: grey empty, dark holds a loop, green playing, red recording, orange overdub.
- **Wind Synth:** turns your guitar into a wind instrument. Turn *Instrument* to pick Sax, Flute, Trumpet or Clarinet (the name shows under the knob). It tracks the pitch you play and your picking strength becomes the breath, so the note swells and sustains instead of dying away like a string. *Breath* sets the airy noise, *Vibrato* the depth (it fades in after each note, like a player), *Tone* opens or darkens the sound, *Octave* shifts it down or up, *Wind* and *Dry* mix the synth with your guitar. Play single notes, one at a time; chords make it drop out. The pedal shows the note it hears. Expect a few milliseconds of tracking delay, most noticeable on the low E string.
- **Bass Synth:** turns your guitar into a bass. It tracks the single note you play and sounds it one or two octaves lower (*Octave* knob). *Voice* picks Finger (round and warm), Pick (bright and clanky) or Synth (saw with a filter that snaps open on each pick). The bass follows your picking, so it hits when you pick and fades as the string does; raise *Sustain* to hold the tail up. *Growl* adds grit, *Tone* brightens or darkens, *Level* sets volume, *Dry* blends in your guitar (0 = bass only). Play single notes, one at a time; chords make it drop out. There is a short tracking delay, most noticeable on the low E string, so play a little ahead or blend in some Dry.
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
[]
I’ll build this as a pitch-tracked bass voice, like the Wind Synth but percussive. First I’m checking how the tracker can be shared.

Mr. Leiser, I added a Bass Synth pedal. It’s in the attached jrig.zip, which still has the Wind Synth. I couldn’t compile it here, so expect a small build error or two on your first build.

It tracks the single note you play and sounds it as a bass one or two octaves lower. Unlike the Wind Synth, the bass follows your picking: it hits when you pick and fades as the string does.

Voice: Finger (round, warm), Pick (bright, clanky) or Synth (a saw wave with a filter that snaps open on each pick).
Octave: -1 or -2.
Sustain: holds the note’s tail up when you want it to ring.
Growl: adds grit.
Tone, Level and Dry: brightness, volume, and how much of your guitar you hear. Dry defaults to 0, so you hear bass only.

The pedal also shows the bass note it is playing.

I tested a Python copy of the algorithm on synthetic guitar notes from E2 to E5. Pitch was within 3 cents, and the output level was steady across voices and octaves. Tracking delay was about 17 ms on notes from A3 up, rising to about 43 ms on the low E and A strings. Play slightly ahead of the beat on the low strings, or raise Dry to hear your guitar’s attack while the bass locks on.

It is monophonic, so chords make it drop out until it locks again. I haven’t heard it, so I can’t say how the voices sound. Tell me what to adjust after you play it.

The pitch tracker is now a shared class in Effects/PitchTracker.cs. The Wind Synth still has its own copy, because I didn’t want to change it without being able to compile. I can switch it over later.

I can also update the jrig skill notes to list the two new pedals if you want that.
[]
Claude
jrig.zip
jrig.zip
ZIP
jrig skill: add effect for wind instrument with option for sax,flute,trumpet,clarinet

Created a file, ran 9 commands
Tracking is accurate (within 3 cents from E2 to E5), but the sax and trumpet peak into the limiter. I’m lowering the wind gain, then packaging.

Shared a file, ran a command
Mr. Leiser, I added a Wind Synth pedal to jrig. The updated project is attached as jrig.zip. I couldn’t compile it here because the sandbox has no .NET SDK, so expect a small build error or two on your first build.

It tracks the single note you play and re-voices it as a sax, flute, trumpet or clarinet. Your picking strength acts as the breath. The note swells and sustains instead of decaying like a string.

The knobs:

Instrument: Sax, Flute, Trumpet or Clarinet. The name shows under the knob, like the Drum Machine’s Style knob. Each one has its own sound, breath noise, attack and vibrato.
Breath: the amount of airy noise.
Vibrato: its depth. It fades in after each note, like a player’s.
Tone: opens or darkens the sound.
Octave: down one octave, normal, or up one.
Wind and Dry: the mix of synth and guitar.
The pedal also shows the note it hears.

I checked the pitch tracking and levels with a Python copy of the algorithm, using synthetic guitar notes from E2 to E5. Pitch was within 3 cents and nothing blew up. Tracking delay was about 17 ms on notes from A3 up and 30 to 43 ms on the low strings, which is typical for guitar-to-wind conversion.

What I couldn’t check by ear is the sound itself. Tell me what to change after you listen: instrument voicing, breath level, or how it feels on low notes. Wind Synth is monophonic, so chords make it drop out. I also updated the README and added the catalog line in Effects/EffectCatalog.cs.

I can also update the jrig skill notes to list the new pedal. Do you want that?

Jrig
ZIP
add effect for bass - to change guitar sound to bass

Shaping the growl distortion and output tone
I’ll build this as a pitch-tracked bass voice, like the Wind Synth but percussive. First I’m checking how the tracker can be shared.

Created 2 files, shared a file, ran 4 commands
Mr. Leiser, I added a Bass Synth pedal. It’s in the attached jrig.zip, which still has the Wind Synth. I couldn’t compile it here, so expect a small build error or two on your first build.

It tracks the single note you play and sounds it as a bass one or two octaves lower. Unlike the Wind Synth, the bass follows your picking: it hits when you pick and fades as the string does.

Voice: Finger (round, warm), Pick (bright, clanky) or Synth (a saw wave with a filter that snaps open on each pick).
Octave: -1 or -2.
Sustain: holds the note’s tail up when you want it to ring.
Growl: adds grit.
Tone, Level and Dry: brightness, volume, and how much of your guitar you hear. Dry defaults to 0, so you hear bass only.
The pedal also shows the bass note it is playing.

I tested a Python copy of the algorithm on synthetic guitar notes from E2 to E5. Pitch was within 3 cents, and the output level was steady across voices and octaves. Tracking delay was about 17 ms on notes from A3 up, rising to about 43 ms on the low E and A strings. Play slightly ahead of the beat on the low strings, or raise Dry to hear your guitar’s attack while the bass locks on.

It is monophonic, so chords make it drop out until it locks again. I haven’t heard it, so I can’t say how the voices sound. Tell me what to adjust after you play it.

The pitch tracker is now a shared class in Effects/PitchTracker.cs. The Wind Synth still has its own copy, because I didn’t want to change it without being able to compile. I can switch it over later.

I can also update the jrig skill notes to list the two new pedals if you want that.

Jrig
ZIP
yes

Drafting an updated skill file with added pedal and architecture notes.
Drafting an updated skill file with added pedal and architecture notes.
Update skill
/jrig
Use when working on the user's guitar effects pedalboard app (jrig) for Windows 11: DSP effects, audio I/O, pedalboard UI, or architecture decisions.

Replaces your current /jrig

Open Content to read the skill before saving

Mr. Leiser, the updated jrig skill is on the card above. Review it and save it from there.

It now lists the Wind Synth and Bass Synth, the shared PitchTracker, the stepped-knob labels, and the current 20-pedal catalog, including the amps, drum machine and eight-slot looper that the old notes were missing. It also records that the synth pedals are checked by Python port but haven’t been listened to. The open items now ask you to listen-test both synths.





Claude is AI and can make mistakes.
Claude finished the response
