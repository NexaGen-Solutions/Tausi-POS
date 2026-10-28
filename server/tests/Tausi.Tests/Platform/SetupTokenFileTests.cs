using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tausi.Infrastructure;
using Tausi.Infrastructure.Platform;

namespace Tausi.Tests.Platform;

public class SetupTokenFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("tausi-token-").FullName;

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_folder, recursive: true);
    }

    private SetupTokenFile TokenFile() => new(Options.Create(new TausiOptions { SetupTokenFile = Path.Combine(_folder, "setup-token.txt") }),
        new FakeHost(_folder));

    [Fact]
    public void A_token_file_the_server_may_not_delete_never_fails_the_reset()
    {
        // reset-admin writes it from an administrator's prompt; the service then may not delete it.
        if (OperatingSystem.IsWindows()) return;
        var file = TokenFile();
        file.Write("K7QM-3HXP");
        File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        FluentActions.Invoking(file.Delete).Should().NotThrow();
        file.Read().Should().Be("K7QM-3HXP", "it is left behind, harmless: the token in the database is cleared");
    }

    [Fact]
    public void Deletes_the_file_when_it_may()
    {
        var file = TokenFile();
        file.Write("K7QM-3HXP");
        file.Delete();
        file.Read().Should().BeNull();
    }

    private sealed class FakeHost(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tausi";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
