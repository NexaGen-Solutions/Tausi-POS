using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tausi.Api.Platform;

/// <summary>The installed settings file (config\appsettings.Production.json), edited as JSON.</summary>
public sealed class SettingsFile(string path)
{
    public string Path { get; } = path;

    public bool Exists => File.Exists(Path);

    public JsonObject Read() =>
        Exists && JsonNode.Parse(File.ReadAllText(Path)) is JsonObject o ? o : new JsonObject();

    public void Write(JsonObject json)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>Sets "A:B:C" to a value, making the objects on the way; null removes it.</summary>
    public static void Set(JsonObject root, string key, JsonNode? value)
    {
        var parts = key.Split(':');
        var node = root;
        foreach (var part in parts[..^1])
        {
            if (node[part] is not JsonObject child)
            {
                child = new JsonObject();
                node[part] = child;
            }
            node = child;
        }
        if (value is null) node.Remove(parts[^1]);
        else node[parts[^1]] = value;
    }

    public static string? Get(JsonObject root, string key)
    {
        JsonNode? node = root;
        foreach (var part in key.Split(':')) node = node is JsonObject o ? o[part] : null;
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
