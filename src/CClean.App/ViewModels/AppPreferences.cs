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
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[16_385];
            var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (length > 16_384) throw new IOException("偏好文件超过 16 KiB，未加载或覆盖原文件。");
            var offset = length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            return JsonSerializer.Deserialize<AppPreferences>(bytes.AsSpan(offset, length - offset))
                ?? throw new JsonException("偏好文件不能为 null，未加载或覆盖原文件。");
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }
    public void Save(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "preferences.json");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(this), System.Text.Encoding.UTF8);
        File.Move(path + ".new", path, true);
    }
}
