using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Tausi.Api.Platform;
using Microsoft.Extensions.Logging;
using Tausi.Infrastructure.Platform;

namespace Tausi.Tests.Platform;

/// <summary>The installed settings file, secrets, the local address and the log files.</summary>
public sealed class ServerHostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tausi-host-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Plain_connection_strings_pass_through_and_secrets_stay_put_off_windows()
    {
        const string plain = "Host=db;Username=tausi;Password=secret";
        MachineSecrets.UnprotectConnectionString(plain).Should().Be(plain);
        if (!OperatingSystem.IsWindows())
        {
            MachineSecrets.Protect("secret").Should().Be("secret", "Linux keeps secrets in a file only the service user reads");
            FluentActions.Invoking(() => MachineSecrets.Unprotect("dpapi:AAAA")).Should().Throw<InvalidOperationException>().WithMessage("*Windows PC*");
        }
        else
        {
            var protectedValue = MachineSecrets.Protect("secret");
            protectedValue.Should().StartWith(MachineSecrets.Prefix);
            MachineSecrets.UnprotectConnectionString($"Host=db;Password={protectedValue}").Should().Be("Host=db;Password=secret");
        }
    }

    [Fact]
    public void The_settings_file_is_edited_in_place()
    {
        var file = new SettingsFile(Path.Combine(_dir, "config", "appsettings.Production.json"));
        var json = file.Read();
        SettingsFile.Set(json, "ConnectionStrings:Tausi", "Server=.\\TAUSI");
        InstallCommands.SetHttpPort(json, 5090);
        file.Write(json);

        var again = file.Read();
        SettingsFile.Get(again, "Urls").Should().Be("http://0.0.0.0:5090");
        SettingsFile.Get(again, "ConnectionStrings:Tausi").Should().Be("Server=.\\TAUSI");
        SettingsFile.Set(again, "ConnectionStrings", null);
        again.ContainsKey("ConnectionStrings").Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null, "http://127.0.0.1:5080")]
    [InlineData("http://0.0.0.0:5090", null, "http://127.0.0.1:5090")]
    [InlineData("http://0.0.0.0:5090", "http://0.0.0.0:6000", "http://127.0.0.1:6000")]
    public void The_local_address_comes_from_the_port_setting(string? urls, string? kestrel, string expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls"] = urls,
            ["Kestrel:Endpoints:Http:Url"] = kestrel,
        }).Build();
        InstallCommands.LocalUrl(config).Should().Be(expected);
    }

    [Fact]
    public void Log_files_older_than_30_days_are_deleted()
    {
        var today = new DateOnly(2026, 10, 25);
        foreach (var d in new[] { 0, 30, 31, 90 })
            File.WriteAllText(Path.Combine(_dir, FileLoggerProvider.FileName(today.AddDays(-d))), "x");
        File.WriteAllText(Path.Combine(_dir, "tausi-notadate.log"), "x");
        FileLoggerProvider.DeleteOld(_dir, today);
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().BeEquivalentTo(
            [FileLoggerProvider.FileName(today), FileLoggerProvider.FileName(today.AddDays(-30)), "tausi-notadate.log"]);
    }

    [Fact]
    public async Task The_file_logger_writes_a_line_per_entry()
    {
        using (var provider = new FileLoggerProvider(_dir))
        {
            var log = provider.CreateLogger("Tausi.Test");
            log.LogError("The backup failed: {Reason}", "disk full");
            await Task.Delay(200);
        }
        var text = await File.ReadAllTextAsync(Directory.GetFiles(_dir, "tausi-*.log").Single());
        text.Should().Contain("[ERR] Tausi.Test: The backup failed: disk full");
    }

    [Fact]
    public void A_json_settings_value_can_be_read_back()
    {
        var json = new JsonObject { ["Kestrel"] = new JsonObject { ["Endpoints"] = new JsonObject() } };
        SettingsFile.Get(json, "Kestrel:Endpoints:Http:Url").Should().BeNull();
    }
}
