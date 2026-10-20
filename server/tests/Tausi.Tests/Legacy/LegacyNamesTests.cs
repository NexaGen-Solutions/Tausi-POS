using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Tausi.Infrastructure.Legacy;
using Tausi.Infrastructure.Seeding;

namespace Tausi.Tests.Legacy;

/// <summary>Names from before the rename to Tausi POS keep working, with a warning.</summary>
public class LegacyNamesTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void The_admin_password_is_read_from_the_new_variable()
    {
        Seeder.AdminPasswordFromEnvironment(Env(("TAUSI_ADMIN_PASSWORD", "new-pass"), ("NEXAGEN_ADMIN_PASSWORD", "old-pass")))
            .Should().Be("new-pass");
    }

    [Fact]
    public void The_old_admin_password_variable_still_works_and_is_warned_about_once()
    {
        LegacyNames.TakeWarnings();
        var read = Env(("NEXAGEN_ADMIN_PASSWORD", "old-pass"));

        Seeder.AdminPasswordFromEnvironment(read).Should().Be("old-pass");
        Seeder.AdminPasswordFromEnvironment(read).Should().Be("old-pass");

        LegacyNames.TakeWarnings().Where(w => w.StartsWith("NEXAGEN_ADMIN_PASSWORD")).Should().ContainSingle()
            .Which.Should().Contain("TAUSI_ADMIN_PASSWORD");
    }

    [Fact]
    public void With_neither_variable_there_is_no_password()
    {
        Seeder.AdminPasswordFromEnvironment(Env()).Should().BeNull();
    }

    [Fact]
    public void Old_settings_override_the_defaults_but_new_settings_win()
    {
        const string prefix = "TAUSI_LEGACY_TEST_";
        Environment.SetEnvironmentVariable(prefix + "Nexagen__DataDirectory", "/var/lib/nexagen");
        Environment.SetEnvironmentVariable(prefix + "Nexagen__Printing__Transport", "File");
        Environment.SetEnvironmentVariable(prefix + "ConnectionStrings__Nexagen", "Host=old");
        Environment.SetEnvironmentVariable(prefix + "Tausi__Printing__Transport", "Real");
        try
        {
            // As in ASP.NET: host variables (prefixed) first, then appsettings.json, then the app's variables.
            var config = new ConfigurationManager();
            config.AddEnvironmentVariables("DOTNET_");
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Tausi"] = "",
                ["Tausi:DataDirectory"] = "data",
                ["Tausi:Printing:Transport"] = "Real",
            });
            config.AddEnvironmentVariables(prefix);

            LegacyNames.ApplyToConfiguration(config);

            config["Tausi:DataDirectory"].Should().Be("/var/lib/nexagen");
            config.GetConnectionString("Tausi").Should().Be("Host=old");
            config["Tausi:Printing:Transport"].Should().Be("Real", "a setting under the new name wins");
        }
        finally
        {
            foreach (var name in new[] { "Nexagen__DataDirectory", "Nexagen__Printing__Transport", "ConnectionStrings__Nexagen", "Tausi__Printing__Transport" })
                Environment.SetEnvironmentVariable(prefix + name, null);
        }
    }

    [Fact]
    public void Without_old_settings_the_configuration_is_left_alone()
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["Tausi:DataDirectory"] = "data" });
        var sources = config.Sources.Count;

        LegacyNames.ApplyToConfiguration(config);

        config.Sources.Should().HaveCount(sources);
        config["Tausi:DataDirectory"].Should().Be("data");
    }

    [Fact]
    public void A_dev_data_folder_left_in_the_old_project_folder_is_copied_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "tausi-legacy-" + Guid.NewGuid().ToString("N"));
        var oldData = Path.Combine(root, "Nexagen.Api", "data");
        Directory.CreateDirectory(Path.Combine(oldData, "keys"));
        File.WriteAllText(Path.Combine(oldData, "pin-pepper.key"), "pepper");
        File.WriteAllText(Path.Combine(oldData, "keys", "key-1.xml"), "<key/>");
        var contentRoot = Path.Combine(root, "Tausi.Api");
        Directory.CreateDirectory(contentRoot);
        try
        {
            LegacyNames.CopyOldDataDirectory("data", contentRoot);

            File.ReadAllText(Path.Combine(contentRoot, "data", "pin-pepper.key")).Should().Be("pepper");
            File.Exists(Path.Combine(contentRoot, "data", "keys", "key-1.xml")).Should().BeTrue();
            File.Exists(Path.Combine(oldData, "pin-pepper.key")).Should().BeTrue("the old folder is left as it was");

            // The new folder exists now, so it is never overwritten.
            File.WriteAllText(Path.Combine(oldData, "pin-pepper.key"), "changed");
            LegacyNames.CopyOldDataDirectory("data", contentRoot);
            File.ReadAllText(Path.Combine(contentRoot, "data", "pin-pepper.key")).Should().Be("pepper");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void An_absolute_data_folder_is_never_touched()
    {
        var root = Path.Combine(Path.GetTempPath(), "tausi-legacy-" + Guid.NewGuid().ToString("N"));
        LegacyNames.CopyOldDataDirectory(Path.Combine(root, "data"), root);
        Directory.Exists(root).Should().BeFalse();
    }
}
