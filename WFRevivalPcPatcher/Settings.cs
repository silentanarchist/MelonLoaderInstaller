using System.Text.Json;

namespace WFRevivalPcPatcher;

/// <summary>The few things kept between runs (%LOCALAPPDATA%\WFRevivalPcPatcher\settings.json).</summary>
internal static class Settings
{
    /// <summary>The server the mod is downloaded from, as the player typed it (host, or host:port). Default: the community server.</summary>
    public static string Server = "wf-revival.silentanarchist.com";
    /// <summary>The last output folder (menu 3 offers it).</summary>
    public static string? LastFolder;

    private static string FilePath => Path.Combine(Program.AppDir, "settings.json");

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.TryGetProperty("server", out var s) && (s.GetString() ?? "").Length > 0) Server = s.GetString()!;
            if (doc.RootElement.TryGetProperty("lastFolder", out var f)) LastFolder = f.GetString();
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Dictionary<string, string?> { ["server"] = Server, ["lastFolder"] = LastFolder }));
        }
        catch { }
    }
}
