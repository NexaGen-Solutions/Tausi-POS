using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.MsSql;
using Tausi.Application.Auditing;
using Tausi.Application.Backups;
using Tausi.Infrastructure;
using Tausi.Infrastructure.Security;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>Its own server and database, on a clock the tests move by hand: backups replace the whole database.</summary>
public sealed class BackupApp : TausiApp
{
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureTestServices(IServiceCollection services) => services.AddSingleton<TimeProvider>(Clock);
}

[CollectionDefinition(Name)]
public sealed class BackupCollection : ICollectionFixture<BackupApp>
{
    public const string Name = "backups";
}

/// <summary>
/// Settings → Backups on PostgreSQL: Back up now, the check, the list and the banners (failed, 48 hours,
/// copy failed), encryption, and a restore that brings back the database and the data folder, after
/// which an existing PIN still logs in.
/// </summary>
[Collection(BackupCollection.Name)]
public class BackupApiTests(BackupApp app)
{
    private static async Task<HttpResponseMessage> Restore(HttpClient admin, string name, object body) =>
        await admin.PostAsJsonAsync($"/api/admin/backups/{name}/restore", body);

    private async Task<string> BackUp(HttpClient admin)
    {
        var response = await admin.PostAsync("/api/admin/backups", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Json()).GetProperty("name").GetString()!;
    }

    private async Task<string> Status(HttpClient client) =>
        (await (await client.GetAsync("/api/backups/status")).Json()).GetProperty("state").GetString()!;

    private TausiOptions Options => app.Services.GetRequiredService<IOptions<TausiOptions>>().Value;

    [Fact]
    public async Task Back_up_now_makes_a_checked_file_with_the_database_the_keys_and_the_pepper()
    {
        var admin = await app.AdminClient();
        var name = await BackUp(admin);
        var list = await (await admin.GetAsync("/api/admin/backups")).Json();
        var row = list.EnumerateArray().Single(b => b.GetProperty("name").GetString() == name);
        row.GetProperty("verified").GetBoolean().Should().BeTrue();
        row.GetProperty("business").GetString().Should().Be("Demo Bar");

        var path = Path.Combine(Options.BackupDirectory, name);
        using (var zip = ZipFile.OpenRead(path))
        {
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            entries.Should().Contain(["manifest.json", "database/tausi.pgdump", "data/secrets/pin-pepper.key"]);
            entries.Should().Contain(e => e.StartsWith("data/keys/", StringComparison.Ordinal), "the data-protection keys");
        }

        (await Status(admin)).Should().Be("ok");
        var health = await (await app.CreateClient().GetAsync("/health")).Json();
        health.GetProperty("backup").GetString().Should().Be("ok");
        health.GetProperty("lastBackupAt").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);
        health.GetProperty("diskFreeBytes").GetInt64().Should().BePositive();
        health.GetProperty("database").GetString().Should().Be("ok");

        var download = await admin.GetAsync($"/api/admin/backups/{name}/download");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).Length.Should().Be((int)new FileInfo(path).Length);
        (await admin.GetAsync("/api/admin/backups/..%2F..%2Fsecrets/download")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Restore_brings_back_the_database_and_the_data_folder_and_an_existing_PIN_still_logs_in()
    {
        var terminal = await app.PairTerminal();
        (await terminal.LoginWithPin(app.Pins["CAROL"])).StatusCode.Should().Be(HttpStatusCode.OK);
        var admin = await app.AdminClient();
        var name = await BackUp(admin);

        // After the backup: a new table, and the PIN pepper lost (as on a new PC).
        var main = (await (await admin.GetAsync("/api/admin/tables")).Json()).GetProperty("areas")[0].GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync("/api/admin/tables/quick-add", new { from = 90, to = 90, areaId = main })).EnsureSuccessStatusCode();
        var pepper = Path.Combine(Options.DataDirectory, "secrets", "pin-pepper.key");
        await File.WriteAllTextAsync(pepper, Convert.ToBase64String(new byte[32]));
        app.Services.GetRequiredService<ServerSecrets>().Reload();
        var lost = await app.PairTerminal();
        (await lost.LoginWithPin(app.Pins["CAROL"])).StatusCode.Should().NotBe(HttpStatusCode.OK, "without its pepper no PIN works");

        var restored = await Restore(admin, name, new { password = (string?)null, confirmBusiness = (string?)null });
        restored.StatusCode.Should().Be(HttpStatusCode.NoContent, await restored.Content.ReadAsStringAsync());

        var after = await terminal.LoginWithPin(app.Pins["CAROL"]);
        after.StatusCode.Should().Be(HttpStatusCode.OK, "the pepper came back with the backup: " + await after.Content.ReadAsStringAsync());
        var tables = (await (await admin.GetAsync("/api/admin/tables")).Json()).GetProperty("tables").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        tables.Should().NotContain("90", "it was added after the backup");
        app.Db(db => db.AuditEntries.Count(a => a.Action == AuditActions.BackupRestored && a.EntityId == name)).Should().Be(1);
        var list = await (await admin.GetAsync("/api/admin/backups")).Json();
        list.EnumerateArray().Should().Contain(b => b.GetProperty("reason").GetString() == "BeforeRestore", "a safety backup was taken first");
    }

    [Fact]
    public async Task A_backup_of_another_business_needs_its_name_typed()
    {
        var admin = await app.AdminClient();
        var name = await BackUp(admin);
        app.Db(db => db.BusinessSettings.ExecuteUpdate(s => s.SetProperty(b => b.Name, "Another Bar")));

        var refused = await Restore(admin, name, new { password = (string?)null, confirmBusiness = (string?)null });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Json()).GetProperty("code").GetString().Should().Be(BackupCodes.OtherBusiness);
        (await Restore(admin, name, new { password = (string?)null, confirmBusiness = "demo  bar" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        app.Db(db => db.BusinessSettings.Single().Name).Should().Be("Demo Bar");
    }

    [Fact]
    public async Task An_encrypted_backup_needs_its_password()
    {
        var admin = await app.AdminClient();
        (await admin.PutAsJsonAsync("/api/admin/backups/settings", new { time = "05:00", copyFolder = (string?)null, encrypt = true, password = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "encryption needs a password");
        (await admin.PutAsJsonAsync("/api/admin/backups/settings", new { time = "05:00", copyFolder = (string?)null, encrypt = true, password = "correct horse" }))
            .EnsureSuccessStatusCode();
        var settings = await (await admin.GetAsync("/api/admin/backups/settings")).Json();
        settings.GetProperty("hasPassword").GetBoolean().Should().BeTrue();
        settings.TryGetProperty("password", out _).Should().BeFalse("the password is never sent back");
        try
        {
            var name = await BackUp(admin);
            (await File.ReadAllBytesAsync(Path.Combine(Options.BackupDirectory, name)))[..2].Should().NotEqual("PK"u8.ToArray());
            var wrong = await admin.PostAsJsonAsync($"/api/admin/backups/{name}/inspect", new { password = "wrong horse" });
            (await wrong.Json()).GetProperty("code").GetString().Should().Be(BackupCodes.WrongPassword);
            var right = await (await admin.PostAsJsonAsync($"/api/admin/backups/{name}/inspect", new { password = "correct horse" })).Json();
            right.GetProperty("encrypted").GetBoolean().Should().BeTrue();
            right.GetProperty("business").GetString().Should().Be("Demo Bar");
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/backups/settings", new { time = "05:00", copyFolder = (string?)null, encrypt = false, password = (string?)null });
        }
    }

    [Fact]
    public async Task A_failed_backup_and_a_failed_copy_show_banners_but_a_copy_failure_keeps_the_backup()
    {
        var admin = await app.AdminClient();
        var dump = Options.Backups.PgDump;
        Options.Backups.PgDump = "/nonexistent/pg_dump";
        try
        {
            var failed = await admin.PostAsync("/api/admin/backups", null);
            failed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Status(admin)).Should().Be("failed");
            var health = await (await app.CreateClient().GetAsync("/health")).Json();
            (health.GetProperty("status").GetString(), health.GetProperty("backup").GetString()).Should().Be(("degraded", "failed"));
        }
        finally
        {
            Options.Backups.PgDump = dump;
        }

        // An unplugged network share as the second copy: the backup here still works.
        (await admin.PutAsJsonAsync("/api/admin/backups/settings", new { time = "05:00", copyFolder = "/proc/tausi-unplugged", encrypt = false, password = (string?)null }))
            .EnsureSuccessStatusCode();
        var copy = Path.Combine(Path.GetTempPath(), "tausi-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var name = await BackUp(admin);
            File.Exists(Path.Combine(Options.BackupDirectory, name)).Should().BeTrue();
            (await Status(admin)).Should().Be("copyFailed");
            var banner = await (await admin.GetAsync("/api/backups/status")).Json();
            banner.GetProperty("message").GetString().Should().Contain("/proc/tausi-unplugged");

            (await admin.PutAsJsonAsync("/api/admin/backups/settings", new { time = "05:00", copyFolder = copy, encrypt = false, password = (string?)null }))
                .EnsureSuccessStatusCode();
            var second = await BackUp(admin);
            File.Exists(Path.Combine(copy, second)).Should().BeTrue();
            (await Status(admin)).Should().Be("ok");
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/backups/settings", new { time = "05:00", copyFolder = (string?)null, encrypt = false, password = (string?)null });
            if (Directory.Exists(copy)) Directory.Delete(copy, true);
        }
    }

    [Fact]
    public async Task No_backup_for_48_hours_shows_the_banner_to_everyone_signed_in()
    {
        var admin = await app.AdminClient();
        await BackUp(admin);
        app.Clock.Advance(TimeSpan.FromHours(47));
        admin = await app.AdminClient();
        (await Status(admin)).Should().Be("ok");
        app.Clock.Advance(TimeSpan.FromHours(2));
        admin = await app.AdminClient();
        var banner = await (await admin.GetAsync("/api/backups/status")).Json();
        banner.GetProperty("state").GetString().Should().Be("overdue");
        banner.GetProperty("message").GetString().Should().Contain("No backup has worked for 2 days");

        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["CAROL"]);
        (await Status(terminal.Http)).Should().Be("overdue", "the Close day screen shows it too");
        await BackUp(admin);
    }

    [Fact]
    public async Task Only_settings_managers_see_and_make_backups()
    {
        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["edna"]);
        (await terminal.Http.GetAsync("/api/admin/backups")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await terminal.Http.PostAsync("/api/admin/backups", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.CreateClient().GetAsync("/api/backups/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task While_a_backup_is_restored_the_POS_answers_that_it_is_restoring()
    {
        var admin = await app.AdminClient();
        using (app.Services.GetRequiredService<MaintenanceGate>().Begin(BackupAdmin.RestoringMessage))
        {
            var busy = await admin.GetAsync("/api/admin/backups");
            busy.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var problem = await busy.Json();
            (problem.GetProperty("code").GetString(), problem.GetProperty("detail").GetString()).Should().Be(("server_restoring", "Server is restoring a backup"));
            (await app.CreateClient().GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        (await admin.GetAsync("/api/admin/backups")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>A server on SQL Server, with a folder SQL Server and the server share for the .bak files.</summary>
public sealed class SqlServerBackupApp : TausiApp
{
    private MsSqlContainer? _sql;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "tausi-sqlwork-" + Guid.NewGuid().ToString("N"));
    public const string WorkInContainer = "/var/opt/mssql/tausi-work";

    protected override string Provider => "SqlServer";

    protected override async Task<string> CreateDatabase()
    {
        Directory.CreateDirectory(_work);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_work, (UnixFileMode)Convert.ToInt32("777", 8));
        _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").WithBindMount(_work, WorkInContainer).Build();
        await _sql.StartAsync();
        return new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = "TausiPOS", CommandTimeout = 120 }.ConnectionString;
    }

    protected override void Settings(IWebHostBuilder builder)
    {
        builder.UseSetting("Tausi:Backups:SqlServerFolder", WorkInContainer);
        builder.UseSetting("Tausi:Backups:SqlServerFolderHere", _work);
    }

    protected override async Task DropDatabase()
    {
        if (_sql is not null) await _sql.DisposeAsync();
        try { Directory.Delete(_work, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerBackupCollection : ICollectionFixture<SqlServerBackupApp>
{
    public const string Name = "sqlserver-backups";
}

/// <summary>The same round trip on SQL Server: BACKUP … WITH CHECKSUM, RESTORE VERIFYONLY, then a restore over the database.</summary>
[Collection(SqlServerBackupCollection.Name)]
public class SqlServerBackupTests(SqlServerBackupApp app)
{
    [Fact]
    public async Task Backup_verify_and_restore_on_SQL_Server_and_an_existing_PIN_still_logs_in()
    {
        var terminal = await app.PairTerminal();
        (await terminal.LoginWithPin(app.Pins["CAROL"])).StatusCode.Should().Be(HttpStatusCode.OK);
        var admin = await app.AdminClient();
        var made = await admin.PostAsync("/api/admin/backups", null);
        made.StatusCode.Should().Be(HttpStatusCode.OK, await made.Content.ReadAsStringAsync());
        var backup = await made.Json();
        backup.GetProperty("verified").GetBoolean().Should().BeTrue();
        var name = backup.GetProperty("name").GetString()!;
        using (var zip = ZipFile.OpenRead(Path.Combine(app.Services.GetRequiredService<IOptions<TausiOptions>>().Value.BackupDirectory, name)))
            zip.Entries.Select(e => e.FullName).Should().Contain("database/tausi.bak");

        var main = (await (await admin.GetAsync("/api/admin/tables")).Json()).GetProperty("areas")[0].GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync("/api/admin/tables/quick-add", new { from = 91, to = 91, areaId = main })).EnsureSuccessStatusCode();

        var restored = await admin.PostAsJsonAsync($"/api/admin/backups/{name}/restore", new { password = (string?)null, confirmBusiness = (string?)null });
        restored.StatusCode.Should().Be(HttpStatusCode.NoContent, await restored.Content.ReadAsStringAsync());

        (await terminal.LoginWithPin(app.Pins["CAROL"])).StatusCode.Should().Be(HttpStatusCode.OK);
        app.Db(db => db.Tables.Any(t => t.Name == "91")).Should().BeFalse();
        app.Db(db => db.AuditEntries.Any(a => a.Action == AuditActions.BackupRestored)).Should().BeTrue();
    }
}
