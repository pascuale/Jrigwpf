namespace Jrig.Effects;

/// <summary>One entry in the pedal library panel.</summary>
public sealed record CatalogEntry(string Name, string Description, string AccentHex, Func<IEffect> Create);

/// <summary>
/// The list of pedals the app knows about. To add a new pedal:
///   1. write a class deriving from EffectBase (see Compressor.cs),
///   2. add one line to <see cref="Entries"/> below.
/// The library panel, drag-and-drop, knobs and preset saving all pick it up automatically.
/// </summary>
public static class EffectCatalog
{
    public static IReadOnlyList<CatalogEntry> Entries { get; } = new[]
    {
        new CatalogEntry("Tuner", "Chromatic tuner. Mutes the signal while you tune.", "#9CCC65", () => new Tuner()),
        new CatalogEntry("Compressor", "Evens out your dynamics. Adds sustain and punch.", "#F2B134", () => new Compressor()),
        new CatalogEntry("Overdrive", "Warm, mid-focused tube-style crunch.", "#43A047", () => new Overdrive()),
        new CatalogEntry("Distortion", "Tight, high-gain distortion with a soft-to-hard voice control.", "#E4572E", () => new Distortion()),
        new CatalogEntry("Octave", "Adds a note an octave down and/or up. Play single notes.", "#8E24AA", () => new Octave()),
        new CatalogEntry("Wah Wah", "Classic heel-to-toe wah. Use Auto for a hands-free sweep.", "#FF7043", () => new WahWah()),
        new CatalogEntry("Envelope Filter", "Auto-wah that follows how hard you pick.", "#FFCA28", () => new EnvelopeFilter()),
        new CatalogEntry("Chorus", "Thick, shimmering doubling.", "#29B6F6", () => new Chorus()),
        new CatalogEntry("Phaser", "Swirling all-pass phase sweep.", "#AB47BC", () => new Phaser()),
        new CatalogEntry("Analog Delay", "Dark, warm repeats that wobble like a bucket-brigade delay.", "#5C6BC0", () => new AnalogDelay()),
        new CatalogEntry("Digital Delay", "Clean, precise repeats up to 2 seconds.", "#26C6DA", () => new DigitalDelay()),
        new CatalogEntry("Marshall Stack", "Roaring British 100 W head into a 4x12 stack. Cascaded gain, tight and cutting.", "#C9A227", () => new MarshallStack()),
        new CatalogEntry("Fender Twin Reverb", "Blackface clean machine. Huge headroom, scooped mids, spring reverb.", "#2E7D9A", () => new FenderTwinReverb()),
        new CatalogEntry("Fender Deluxe Reverb", "Smaller blackface 1x12 that sags and breaks up earlier. Spring reverb.", "#3F9BB5", () => new FenderDeluxeReverb()),
        new CatalogEntry("Fender '57 Twin-Amp", "Tweed Twin. Loose, compressed, fuzzy breakup when pushed.", "#D9B26F", () => new Fender57TwinAmp()),
        new CatalogEntry("Mesa/Boogie", "Thick, tight multi-stage lead channel with a Contour mid scoop.", "#B03A2E", () => new MesaBoogie()),
        new CatalogEntry("Looper", "Record a phrase, loop it and overdub up to 60 seconds.", "#EC407A", () => new Looper()),
    };

    public static CatalogEntry? Find(string typeName) =>
        Entries.FirstOrDefault(e => e.Name == typeName);
}
