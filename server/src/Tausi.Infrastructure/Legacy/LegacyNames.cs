using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;

namespace Tausi.Infrastructure.Legacy;

/// <summary>
/// Names from before the product was renamed from Nexagen POS to Tausi POS. Installs set up with the
/// old names keep working: each old name is still read when the new one is not set, with a warning
/// once per name so it can be renamed at leisure.
/// </summary>
public static class LegacyNames
{
    public const string OldSection = "Nexagen";
    public const string OldConnectionString = "ConnectionStrings:Nexagen";
    public const string NewConnectionString = "ConnectionStrings:" + TausiOptions.ConnectionStringName;

    private static readonly ConcurrentDictionary<string, bool> Warned = new();
    private static readonly ConcurrentQueue<string> Pending = new();

    /// <summary>Old names used since start-up, waiting to be logged (see <see cref="TakeWarnings"/>).</summary>
    public static IReadOnlyList<string> TakeWarnings()
    {
        var list = new List<string>();
        while (Pending.TryDequeue(out var warning)) list.Add(warning);
        return list;
    }

    /// <summary>The environment variable <paramref name="name"/>, or else the old <paramref name="oldName"/>.</summary>
    public static string? Environment(string name, string oldName, Func<string, string?>? read = null)
    {
        read ??= System.Environment.GetEnvironmentVariable;
        var value = read(name);
        if (!string.IsNullOrEmpty(value)) return value;
        var old = read(oldName);
        if (!string.IsNullOrEmpty(old)) Warn(oldName, name);
        return string.IsNullOrEmpty(old) ? value : old;
    }

    /// <summary>
    /// Copies settings under the old section "Nexagen" (and the connection string "Nexagen") to the
    /// new names. They are added just before the app's environment variables, so they override the defaults
    /// in appsettings.json while a setting given under the new name still wins.
    /// </summary>
    public static void ApplyToConfiguration(IConfigurationManager config)
    {
        var mapped = new Dictionary<string, string?>();
        foreach (var (key, value) in ((IConfiguration)config).AsEnumerable())
        {
            if (value is null) continue;
            string? newKey = null;
            if (key.Equals(OldConnectionString, StringComparison.OrdinalIgnoreCase)) newKey = NewConnectionString;
            else if (key.StartsWith(OldSection + ":", StringComparison.OrdinalIgnoreCase))
                newKey = TausiOptions.Section + key[OldSection.Length..];
            if (newKey is null || string.IsNullOrEmpty(value)) continue;
            mapped[newKey] = value;
            Warn(key, newKey);
        }
        if (mapped.Count == 0) return;

        var source = new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource { InitialData = mapped };
        // ASP.NET reads environment variables twice: prefixed host settings before appsettings.json and the
        // app's own after it. Go just before the last one.
        var index = config.Sources.ToList().FindLastIndex(s => s.GetType().Name == "EnvironmentVariablesConfigurationSource");
        if (index < 0) config.Sources.Add(source);
        else config.Sources.Insert(index, source);
    }

    /// <summary>
    /// A development checkout keeps server secrets (PIN pepper, signing keys) in a data folder next to
    /// the project, which git does not move when the project folder is renamed. When the data folder is
    /// missing and the old project's one exists, copy it, so PINs keep working.
    /// </summary>
    public static void CopyOldDataDirectory(string dataDirectory, string contentRoot)
    {
        if (Path.IsPathRooted(dataDirectory)) return;
        var target = Path.GetFullPath(dataDirectory, contentRoot);
        if (Directory.Exists(target)) return;
        var parent = Path.GetDirectoryName(Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar));
        if (parent is null) return;
        var old = Path.GetFullPath(dataDirectory, Path.Combine(parent, "Nexagen.Api"));
        if (!Directory.Exists(old)) return;

        foreach (var file in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(old, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        Pending.Enqueue($"Copied the data folder from {old} to {target} (the project was renamed to Tausi). The old folder can be deleted.");
    }

    private static void Warn(string oldName, string newName)
    {
        if (Warned.TryAdd(oldName, true))
            Pending.Enqueue($"{oldName} is an old setting name from before the rename to Tausi POS. Use {newName} instead.");
    }
}
