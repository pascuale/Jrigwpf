using System.IO;
using System.Text.Json;

namespace Jrig.Persistence;

public sealed class PedalState
{
    public string Type { get; set; } = "";
    public bool Active { get; set; } = true;
    public Dictionary<string, float> Parameters { get; set; } = new();
}

public sealed class BoardState
{
    public List<PedalState> Pedals { get; set; } = new();
}

public sealed class AppSettings
{
    public string? Driver { get; set; }
    public int SampleRate { get; set; } = 48000;
    public int InputChannel { get; set; }
    public double InputGainDb { get; set; }
    public double OutputGainDb { get; set; }
    public int SourceIndex { get; set; }          // 0 = guitar through ASIO, others = simulated guitar modes
    public string? SimFile { get; set; }          // last audio file used by the simulated guitar
}

public sealed class AppState
{
    public AppSettings Settings { get; set; } = new();
    public BoardState Board { get; set; } = new();
}

/// <summary>
/// Remembers your board and audio settings between sessions
/// (%AppData%\jrig\state.json) and saves / loads named presets.
/// </summary>
public static class StateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string StatePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "jrig", "state.json");

    public static AppState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), Options) ?? new AppState();
        }
        catch
        {
            // A damaged state file should never stop the app from starting.
        }
        return new AppState();
    }

    public static void SaveState(AppState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Options));
        }
        catch
        {
            // Not being able to save settings is not worth interrupting the user for.
        }
    }

    public static BoardState? LoadBoard(string path) =>
        JsonSerializer.Deserialize<BoardState>(File.ReadAllText(path), Options);

    public static void SaveBoard(string path, BoardState board) =>
        File.WriteAllText(path, JsonSerializer.Serialize(board, Options));
}
