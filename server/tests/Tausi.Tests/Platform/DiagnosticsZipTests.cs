using System.IO.Compression;
using FluentAssertions;
using Tausi.Api.Endpoints;
using Tausi.Application.Backups;
using Tausi.Application.Support;

namespace Tausi.Tests.Platform;

public class DiagnosticsZipTests : IDisposable
{
    private readonly string _logs = Directory.CreateTempSubdirectory("tausi-diag-").FullName;

    public void Dispose() => Directory.Delete(_logs, recursive: true);

    [Fact]
    public void Holds_the_health_summary_system_and_the_last_seven_days_of_logs_only()
    {
        var now = new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.FromHours(3));
        File.WriteAllText(Path.Combine(_logs, "tausi-20261028.log"), "today");
        File.WriteAllText(Path.Combine(_logs, "tausi-20261021.log"), "seven days ago");
        File.WriteAllText(Path.Combine(_logs, "tausi-20261020.log"), "eight days ago");
        File.WriteAllText(Path.Combine(_logs, "appsettings.Production.json"), "secret");

        var health = new SystemEndpoints.Health("ok", "1.0.0", "POS-SERVER", "ok", 1, null, "none", null, "Trial", "1.0.0", []);
        var summary = new DiagnosticsSummary(now, "Demo Bar", [], [], [], new BackupHealth("none", null, null), [], 0, null);

        using var zip = new ZipArchive(new MemoryStream(DiagnosticsZip.Build(health, summary, _logs, now, "Postgres")));
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
            ["health.json", "summary.json", "system.json", "logs/tausi-20261021.log", "logs/tausi-20261028.log"]);
    }

    [Fact]
    public void Works_without_a_log_folder()
    {
        var health = new SystemEndpoints.Health("ok", "1.0.0", "POS-SERVER", "ok", 1, null, "none", null, null, "1.0.0", []);
        var summary = new DiagnosticsSummary(DateTimeOffset.UnixEpoch, "Demo Bar", [], [], [], new BackupHealth("none", null, null), [], 0, null);

        using var zip = new ZipArchive(new MemoryStream(DiagnosticsZip.Build(health, summary, null, DateTimeOffset.UnixEpoch, "SqlServer")));
        zip.Entries.Should().HaveCount(3);
    }
}
