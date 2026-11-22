using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaimaiTouchTool.Theming;

public sealed class UserPreferences
{
    public string Theme { get; set; } = "dark";
    public string? Language { get; set; }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MaimaiTouchTool",
        "preferences.json");

    public static UserPreferences Load(string? path = null)
    {
        var targetPath = path ?? DefaultPath;
        try
        {
            if (File.Exists(targetPath))
            {
                var json = File.ReadAllText(targetPath);
                return JsonSerializer.Deserialize<UserPreferences>(json, Options) ?? new UserPreferences();
            }
        }
        catch
        {
            // Fallback to default on any read/parse error
        }
        return new UserPreferences();
    }

    public void Save(string? path = null)
    {
        var targetPath = path ?? DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, targetPath, true);
        }
        catch
        {
            // Silent fallback if IO fails
        }
    }
}
