using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Jrig.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? Name + "  (Windows default)" : Name;
}

/// <summary>Lists Windows (WASAPI) audio devices and writes a detailed report for the log.</summary>
public static class DeviceScanner
{
    public static List<AudioDeviceInfo> List(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? defId = null;
            try { defId = en.GetDefaultAudioEndpoint(flow, Role.Console).ID; } catch { /* no default */ }

            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                result.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == defId));
        }
        catch (Exception ex)
        {
            DiagLog.Error($"Could not list {flow} devices", ex);
        }
        return result;
    }

    /// <summary>Everything Windows says about every audio endpoint and ASIO driver. Written to the log.</summary>
    public static string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== AUDIO DEVICE REPORT ===");
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
            {
                sb.AppendLine($"--- {(flow == DataFlow.Capture ? "INPUT (recording)" : "OUTPUT (playback)")} devices ---");
                string? defId = null;
                try { defId = en.GetDefaultAudioEndpoint(flow, Role.Console).ID; } catch { /* none */ }

                foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.All))
                {
                    sb.AppendLine($"* {Safe(() => d.FriendlyName)}{(d.ID == defId ? "   [Windows default]" : "")}");
                    sb.AppendLine($"    state: {Safe(() => d.State.ToString())}   id: {d.ID}");
                    if (d.State != DeviceState.Active) continue;

                    try
                    {
                        var vol = d.AudioEndpointVolume;
                        sb.AppendLine($"    Windows volume: {vol.MasterVolumeLevelScalar * 100:F0}%   muted: {vol.Mute}");
                    }
                    catch (Exception ex) { sb.AppendLine($"    volume: unavailable ({ex.Message})"); }

                    try
                    {
                        using var client = d.AudioClient;
                        var mix = client.MixFormat;
                        sb.AppendLine($"    shared-mode mix format: {Describe(mix)}");
                        sb.Append("    exclusive-mode formats: ");
                        var ok = new List<string>();
                        foreach (int rate in new[] { 44100, 48000 })
                        foreach (int ch in new[] { 1, 2 })
                        foreach (int bits in new[] { 16, 24, 32 })
                        {
                            try
                            {
                                if (client.IsFormatSupported(AudioClientShareMode.Exclusive, new WaveFormat(rate, bits, ch)))
                                    ok.Add($"{rate}/{bits}bit/{ch}ch");
                            }
                            catch { /* not supported */ }
                        }
                        sb.AppendLine(ok.Count == 0 ? "none found" : string.Join(", ", ok));
                    }
                    catch (Exception ex) { sb.AppendLine($"    format query failed: {ex.Message}"); }

                    if (flow == DataFlow.Capture)
                    {
                        try
                        {
                            sb.AppendLine($"    Windows meter peak right now: {d.AudioMeterInformation.MasterPeakValue:F4}  (should move when you strum)");
                        }
                        catch { /* ignore */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("Device enumeration failed: " + ex);
        }

        sb.AppendLine("--- ASIO drivers ---");
        try
        {
            var names = AsioNames();
            sb.AppendLine(names.Length == 0 ? "(none installed)" : string.Join(Environment.NewLine, names.Select(n => "* " + n)));
        }
        catch (Exception ex) { sb.AppendLine("ASIO enumeration failed: " + ex.Message); }
        sb.AppendLine("=== END DEVICE REPORT ===");
        return sb.ToString();
    }

    private static string[] AsioNames() => AudioEngine.GetDriverNames();

    public static string Describe(WaveFormat f)
    {
        string enc = f.Encoding == WaveFormatEncoding.Extensible && f is WaveFormatExtensible ext
            ? (ext.SubFormat == AudioEngine.IeeeFloatSubFormat ? "float" : "pcm")
            : f.Encoding.ToString();
        return $"{f.SampleRate} Hz, {f.BitsPerSample}-bit {enc}, {f.Channels} ch";
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch (Exception ex) { return "(" + ex.Message + ")"; }
    }
}
