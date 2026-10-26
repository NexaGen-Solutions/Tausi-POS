using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Tausi.Application.Updates;
using Tausi.Domain.Common;
using Tausi.Domain.Service;
using Tausi.Licensing;
using Tausi.Tests.Tickets;

namespace Tausi.Tests.Platform;

/// <summary>Server updates: the feed, the signature check, the closed-day rule and USB packages; the terminals' server-first rule.</summary>
public class UpdateTests : ApprovalsFixtureBase
{
    /// <summary>The tests' update key (its public half is <see cref="ServerUpdates.TestPublicKey"/>).</summary>
    public const string TestPrivateKey = "RO/SXrz25PX/0ctynF7/dc0oN3fsH7SDVEn1GUulCs4=";

    private static string Next => "99.0.0";

    private async Task Admin() => await F.LoginOn("Administrator", "Counter B");

    private static (string Path, string Sha) Installer(string text = "an installer")
    {
        var path = Path.Combine(Path.GetTempPath(), $"tausi-setup-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, text);
        return (path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static string Sign(string version, string sha) => UpdateSignatures.Sign(Convert.FromBase64String(TestPrivateKey), version, sha);

    private void Feed(string version, string? signature = null, string? sha = null)
    {
        var (path, actual) = Installer();
        F.Updates.Files["https://updates.example/setup.exe"] = path;
        F.Updates.Feed = new UpdateFeed(version, "0.1.0", null, "Faster reports.",
            new Dictionary<string, UpdateAsset> { ["windows-x64"] = new("https://updates.example/setup.exe", sha ?? actual, signature ?? Sign(version, actual), null) });
    }

    private async Task CloseDay()
    {
        foreach (var w in F.Db.WorkPeriods)
        {
            w.ClosedAt = F.Clock.GetUtcNow();
            w.IsOpen = false;
        }
        await F.Db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("1.2.3", "1.2.10", -1)]
    [InlineData("v1.10.0", "1.9.9", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("1.0.0-beta", "1.0.0", 0)]
    [InlineData("junk", "0.0.1", -1)]
    public void Versions_compare_by_number(string a, string b, int sign) => Math.Sign(SemVer.Compare(a, b)).Should().Be(sign);

    [Fact]
    public void A_signature_covers_the_version_and_the_installer()
    {
        var key = Convert.FromBase64String(ServerUpdates.TestPublicKey);
        var sig = Sign("1.2.0", new string('a', 64));
        UpdateSignatures.Verify(key, "1.2.0", new string('a', 64), sig).Should().BeTrue();
        UpdateSignatures.Verify(key, "1.3.0", new string('a', 64), sig).Should().BeFalse("another version");
        UpdateSignatures.Verify(key, "1.2.0", new string('b', 64), sig).Should().BeFalse("another installer");
        UpdateSignatures.Verify(Convert.FromBase64String(ServerUpdates.ProductionPublicKey), "1.2.0", new string('a', 64), sig).Should().BeFalse("another key");
        UpdateSignatures.Verify(key, "1.2.0", new string('a', 64), null).Should().BeFalse("unsigned");
    }

    [Fact]
    public async Task Check_says_what_is_new_and_copes_without_internet()
    {
        await Admin();
        var offline = await F.Get<ServerUpdates>().Check(None);
        offline.Available.Should().BeFalse();
        offline.Error.Should().Contain("USB");
        Feed(Next);
        var check = await F.Get<ServerUpdates>().Check(None);
        (check.Latest, check.Available, check.Notes, check.DayOpen).Should().Be((Next, true, "Faster reports.", true));
    }

    [Fact]
    public async Task An_update_waits_for_the_day_to_close_unless_forced_with_UPDATE()
    {
        await Admin();
        Feed(Next);
        await FluentActions.Awaiting(() => F.Get<ServerUpdates>().Apply(new ApplyUpdateRequest(false, null), None))
            .Should().ThrowAsync<DomainException>().Where(e => e.Code == UpdateCodes.DayOpen);
        await FluentActions.Awaiting(() => F.Get<ServerUpdates>().Apply(new ApplyUpdateRequest(true, "update please"), None))
            .Should().ThrowAsync<DomainException>().Where(e => e.Code == UpdateCodes.DayOpen);
        F.UpdateInstaller.Launched.Should().BeEmpty();

        await F.Get<ServerUpdates>().Apply(new ApplyUpdateRequest(true, "UPDATE"), None);
        F.UpdateInstaller.Launched.Should().ContainSingle().Which.Version.Should().Be(Next);
        F.Read().AuditEntries.Should().Contain(a => a.Action == Application.Auditing.AuditActions.ServerUpdateStarted && a.EntityId == Next);
    }

    [Fact]
    public async Task An_installer_that_doesnt_match_its_signature_never_runs()
    {
        await Admin();
        await CloseDay();
        Feed(Next, signature: Sign("98.0.0", new string('0', 64)));
        await FluentActions.Awaiting(() => F.Get<ServerUpdates>().Apply(new ApplyUpdateRequest(false, null), None))
            .Should().ThrowAsync<DomainException>().Where(e => e.Code == UpdateCodes.BadSignature);
        Feed(Next, sha: new string('0', 64));
        await FluentActions.Awaiting(() => F.Get<ServerUpdates>().Apply(new ApplyUpdateRequest(false, null), None))
            .Should().ThrowAsync<DomainException>().Where(e => e.Code == UpdateCodes.BadSignature);
        F.UpdateInstaller.Launched.Should().BeEmpty();

        Feed(Next);
        await F.Get<ServerUpdates>().Apply(new ApplyUpdateRequest(false, null), None);
        F.UpdateInstaller.Launched.Should().ContainSingle();
    }

    [Fact]
    public async Task Only_settings_managers_update_and_only_where_the_server_can()
    {
        Feed(Next);
        await FluentActions.Awaiting(() => F.Get<ServerUpdates>().Check(None)).Should().ThrowAsync<Application.Security.PermissionDeniedException>();
        await Admin();
        await CloseDay();
        F.UpdateInstaller.Why = "On Linux, update with the new package.";
        await FluentActions.Awaiting(() => F.Get<ServerUpdates>().Apply(new ApplyUpdateRequest(false, null), None))
            .Should().ThrowAsync<DomainException>().WithMessage("On Linux*");
    }

    private static MemoryStream Package(string version, string platform, Func<string, string>? signFor = null)
    {
        var (path, sha) = Installer("usb installer " + version);
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntryFromFile(path, "TausiPOS-Server-Setup.exe");
            using var info = new StreamWriter(zip.CreateEntry("update.json").Open());
            info.Write(JsonSerializer.Serialize(new UpdatePackageInfo(version, platform, "TausiPOS-Server-Setup.exe", sha,
                signFor?.Invoke(sha) ?? Sign(version, sha), null), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task A_package_from_a_USB_drive_is_checked_the_same_way()
    {
        await Admin();
        await CloseDay();
        await FluentActions.Awaiting(() => F.Get<ServerUpdates>().Upload(Package(Next, "linux-x64"), new ApplyUpdateRequest(false, null), None))
            .Should().ThrowAsync<DomainException>().WithMessage("*for linux-x64*");
        await FluentActions.Awaiting(() => F.Get<ServerUpdates>().Upload(Package("0.0.1", "windows-x64"), new ApplyUpdateRequest(false, null), None))
            .Should().ThrowAsync<DomainException>().WithMessage("*already has*");
        await FluentActions.Awaiting(() => F.Get<ServerUpdates>().Upload(Package(Next, "windows-x64", sha => Sign("1.0.0", sha)), new ApplyUpdateRequest(false, null), None))
            .Should().ThrowAsync<DomainException>().Where(e => e.Code == UpdateCodes.BadSignature);
        await FluentActions.Awaiting(() => F.Get<ServerUpdates>().Upload(new MemoryStream("not a zip"u8.ToArray()), new ApplyUpdateRequest(false, null), None))
            .Should().ThrowAsync<DomainException>().WithMessage("*isn't a Tausi POS update package*");

        await F.Get<ServerUpdates>().Upload(Package(Next, "windows-x64"), new ApplyUpdateRequest(false, null), None);
        F.UpdateInstaller.Launched.Should().ContainSingle().Which.Version.Should().Be(Next);
    }

    // ---- Terminals ----

    private sealed class Files(TerminalUpdate? update) : ITerminalUpdateFiles
    {
        public TerminalUpdate? Current() => update;
        public string? PathOf(string file) => null;
    }

    [Fact]
    public void Terminals_are_never_offered_a_version_newer_than_their_server()
    {
        var update = new TerminalUpdate("1.3.0", null, null, "TausiPOS-Setup.exe", "sig");
        new TerminalUpdateFeed(new Files(update)).Offer("1.3.0").Should().Be(update);
        new TerminalUpdateFeed(new Files(update)).Offer("1.4.1").Should().Be(update);
        new TerminalUpdateFeed(new Files(update)).Offer("1.2.9").Should().BeNull("server first");
        new TerminalUpdateFeed(new Files(null)).Offer("1.3.0").Should().BeNull();
    }

    [Theory]
    [InlineData("0.0.9", true)]
    [InlineData("0.1.0", false)]
    [InlineData("2.0.0", false)]
    [InlineData(null, false)]
    [InlineData("dev", false)]
    public void A_terminal_older_than_the_minimum_must_update(string? version, bool tooOld) =>
        TerminalVersions.TooOld(version).Should().Be(tooOld);
}
