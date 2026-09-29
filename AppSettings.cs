using System;
using System.IO;
using System.Text.Json;

namespace ClaudeUsageWidget;

public sealed class AppSettings
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageWidget");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Topmost { get; set; }
    public bool Locked { get; set; }
    public bool Compact { get; set; }
    public bool CloseHintShown { get; set; }
    public bool Visible { get; set; } = true;
    public int PollMinutes { get; set; } = 5;
    public UsageSnapshot? LastSnapshot { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings file: fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Non-fatal: settings just won't persist.
        }
    }
}
