using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Tausi.Application.Support;

namespace Tausi.Api.Endpoints;

/// <summary>
/// The diagnostics zip: health.json, summary.json (terminals, printers, failed prints, backups),
/// system.json (versions, OS, database type) and the last <see cref="LogDays"/> days of log files.
/// Never the settings file, connection strings, keys or the database.
/// </summary>
public static class DiagnosticsZip
{
    public const int LogDays = 7;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static byte[] Build(SystemEndpoints.Health health, DiagnosticsSummary summary, string? logFolder, DateTimeOffset now, string provider)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "health.json", JsonSerializer.Serialize(health, Json));
            Add(zip, "summary.json", JsonSerializer.Serialize(summary, Json));
            Add(zip, "system.json", JsonSerializer.Serialize(new
            {
                Server = SystemEndpoints.Version,
                Os = RuntimeInformation.OSDescription,
                Runtime = RuntimeInformation.FrameworkDescription,
                Machine = Environment.MachineName,
                Database = provider,
                CollectedAt = now,
            }, Json));
            if (logFolder is not null && Directory.Exists(logFolder))
            {
                var since = DateOnly.FromDateTime(now.Date).AddDays(-LogDays);
                foreach (var path in Directory.EnumerateFiles(logFolder, "tausi-*.log").Order())
                {
                    var name = Path.GetFileName(path);
                    // tausi-20261025.log, as the file logger names them.
                    if (!DateOnly.TryParseExact(name["tausi-".Length..^".log".Length], "yyyyMMdd", out var day) || day < since) continue;
                    // The day's file is still being written: share it.
                    using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var target = zip.CreateEntry($"logs/{name}", CompressionLevel.Optimal).Open();
                    source.CopyTo(target);
                }
            }
        }
        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(text);
    }
}
