using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Tausi.Application.Backups;
using Tausi.Domain.Common;
using Tausi.Infrastructure.Backups;

namespace Tausi.Tests.Platform;

/// <summary>The .tausibak format: checksums, encryption with the right and the wrong password, the retention rule and the schedule.</summary>
public sealed class BackupFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tausi-backupfile-").FullName;
    private static readonly CancellationToken None = CancellationToken.None;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Source(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static BackupManifest Header => new(BackupManifest.CurrentFormat, "Tausi POS", "1.0.0", "20261025_SetupWizard", "Postgres",
        "Mau's Village", new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.Zero), BackupReason.Nightly, []);

    private async Task<string> Zip()
    {
        var zip = Path.Combine(_dir, "b.zip");
        await BackupFile.Write(zip, Header,
            [("database/tausi.pgdump", Source("db", "the database")), ("data/secrets/pin-pepper.key", Source("pepper", "pepper")), ("data/keys/key-1.xml", Source("key", "<key/>"))],
            None);
        return zip;
    }

    [Fact]
    public async Task A_backup_holds_the_database_the_keys_and_a_manifest_with_checksums()
    {
        var manifest = await BackupFile.Check(await Zip(), None);
        manifest.Business.Should().Be("Mau's Village");
        manifest.Files.Select(f => f.Path).Should().Equal("database/tausi.pgdump", "data/secrets/pin-pepper.key", "data/keys/key-1.xml");
        manifest.Files[1].Sha256.Should().HaveLength(64);
    }

    [Fact]
    public async Task A_changed_file_fails_the_check()
    {
        var zip = await Zip();
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            archive.GetEntry("data/secrets/pin-pepper.key")!.Delete();
            await using var w = archive.CreateEntry("data/secrets/pin-pepper.key").Open();
            await w.WriteAsync("other"u8.ToArray());
        }
        await FluentActions.Awaiting(() => BackupFile.Check(zip, None)).Should().ThrowAsync<DomainException>()
            .WithMessage("*pin-pepper.key doesn't match its checksum*");
    }

    [Fact]
    public async Task A_file_that_isnt_a_backup_says_so()
    {
        await FluentActions.Awaiting(() => BackupFile.Check(Source("x.tausibak", "hello"), None)).Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == BackupCodes.Invalid);
    }

    [Fact]
    public async Task An_encrypted_backup_opens_with_its_password_only()
    {
        var zip = await Zip();
        var encrypted = Path.Combine(_dir, "b.tausibak");
        await BackupFile.Encrypt(zip, encrypted, "correct horse", None);
        BackupFile.IsEncrypted(encrypted).Should().BeTrue();
        BackupFile.IsEncrypted(zip).Should().BeFalse();
        (await File.ReadAllBytesAsync(encrypted))[..2].Should().NotEqual("PK"u8.ToArray(), "it isn't a readable zip");

        await FluentActions.Awaiting(() => BackupFile.Decrypt(encrypted, Path.Combine(_dir, "wrong.zip"), "wrong horse", None))
            .Should().ThrowAsync<DomainException>().Where(e => e.Code == BackupCodes.WrongPassword);
        File.Exists(Path.Combine(_dir, "wrong.zip")).Should().BeFalse("nothing is decrypted before the password checks out");
        await FluentActions.Awaiting(() => BackupFile.Decrypt(encrypted, Path.Combine(_dir, "none.zip"), null, None))
            .Should().ThrowAsync<DomainException>().WithMessage("*encrypted*");

        var plain = Path.Combine(_dir, "plain.zip");
        await BackupFile.Decrypt(encrypted, plain, "correct horse", None);
        (await BackupFile.Check(plain, None)).Business.Should().Be("Mau's Village");
    }

    [Fact]
    public async Task A_damaged_encrypted_backup_is_caught_before_decrypting()
    {
        var encrypted = Path.Combine(_dir, "b.tausibak");
        await BackupFile.Encrypt(await Zip(), encrypted, "correct horse", None);
        var bytes = await File.ReadAllBytesAsync(encrypted);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(encrypted, bytes);
        await FluentActions.Awaiting(() => BackupFile.Decrypt(encrypted, Path.Combine(_dir, "out.zip"), "correct horse", None))
            .Should().ThrowAsync<DomainException>().WithMessage("*damaged*");
    }

    [Fact]
    public async Task A_backup_from_a_newer_version_says_to_update_first()
    {
        var zip = Path.Combine(_dir, "new.zip");
        await BackupFile.Write(zip, Header with { Format = BackupManifest.CurrentFormat + 1, Version = "9.0.0" }, [], None);
        await FluentActions.Awaiting(() => BackupFile.Check(zip, None)).Should().ThrowAsync<DomainException>().WithMessage("*newer Tausi POS (9.0.0)*");
    }

    [Fact]
    public void The_manifest_is_readable_json()
    {
        var json = JsonSerializer.Serialize(Header, BackupFile.Json);
        json.Should().Contain("\"reason\": \"Nightly\"").And.Contain("\"business\": \"Mau's Village\"");
    }

    // ---- Keeping ----

    [Fact]
    public void Keeps_the_last_14_nightly_the_first_of_each_month_for_12_months_and_others_for_30_days()
    {
        var now = new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.Zero);
        var backups = new List<(string, DateTimeOffset, BackupReason)>();
        // A nightly backup every day for 400 days.
        for (var d = 0; d < 400; d++) backups.Add(($"n{d}", now.AddDays(-d), BackupReason.Nightly));
        backups.Add(("manual-recent", now.AddDays(-3), BackupReason.Manual));
        backups.Add(("manual-old", now.AddDays(-40), BackupReason.Manual));
        backups.Add(("upgrade-recent", now.AddDays(-29), BackupReason.Upgrade));

        var keep = BackupEngine.Keep(backups, now);
        Enumerable.Range(0, 14).Select(d => $"n{d}").Should().BeSubsetOf(keep);
        keep.Should().NotContain("n14");
        // The first of each month back to November 2025 (12 months including this one).
        var firsts = backups.Where(b => b.Item3 == BackupReason.Nightly)
            .GroupBy(b => (b.Item2.Year, b.Item2.Month)).Where(g => new DateTime(g.Key.Year, g.Key.Month, 1) >= new DateTime(2025, 11, 1))
            .Select(g => g.MinBy(b => b.Item2).Item1).ToList();
        firsts.Should().HaveCount(12).And.BeSubsetOf(keep);
        keep.Should().NotContain(backups.Where(b => b.Item2 < new DateTimeOffset(2025, 11, 1, 0, 0, 0, TimeSpan.Zero)).Select(b => b.Item1));
        keep.Should().Contain(["manual-recent", "upgrade-recent"]).And.NotContain("manual-old");
        keep.Should().HaveCount(14 + 12 + 2, "the last 14 days, 12 firsts of the month (1 October is older than 14 days), 2 recent others");
    }

    [Theory]
    [InlineData("2026-10-25T04:59:00+03:00", null)]
    [InlineData("2026-10-25T05:00:00+03:00", "2026-10-25")]
    [InlineData("2026-10-25T23:30:00+03:00", "2026-10-25")]
    public void The_nightly_backup_is_due_from_its_time_in_business_time(string now, string? day)
    {
        BackupScheduler.Due(DateTimeOffset.Parse(now), new TimeOnly(5, 0)).Should().Be(day is null ? null : DateOnly.Parse(day));
    }
}
