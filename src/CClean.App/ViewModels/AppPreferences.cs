using System.IO;
using System.Text.Json;

namespace CClean.App.ViewModels;

public sealed class AppPreferences
{
    public bool Dark { get; set; }
    public string DiscoveryRoot { get; set; } = "";
    public static AppPreferences Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "preferences.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16_384) return new();
            return JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "preferences.json");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(this), System.Text.Encoding.UTF8);
        File.Move(path + ".new", path, true);
    }
}
