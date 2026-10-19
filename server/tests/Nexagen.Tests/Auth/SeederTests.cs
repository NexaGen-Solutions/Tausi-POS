using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nexagen.Application.Auth;
using Nexagen.Domain.Devices;
using Nexagen.Infrastructure.Seeding;
using Nexagen.Tests.Support;

namespace Nexagen.Tests.Auth;

public class SeederTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private Seeder Seeder() => new(_db.Context, new FastPinHasher(), NullLogger<Seeder>.Instance);

    [Fact]
    public async Task In_development_demo_printers_print_to_files()
    {
        await Seeder().SeedDemo("pw", CancellationToken.None, filePrinters: true);

        (await _db.NewContext().Printers.ToListAsync()).Should().NotBeEmpty()
            .And.OnlyContain(p => p.ConnectionMode == PrinterConnection.File);
    }

    [Fact]
    public async Task Elsewhere_they_are_windows_share_placeholders()
    {
        await Seeder().SeedDemo("pw", CancellationToken.None);

        (await _db.NewContext().Printers.ToListAsync()).Should()
            .OnlyContain(p => p.ConnectionMode == PrinterConnection.WindowsShare && p.Address.StartsWith(@"\\"));
    }

    [Fact]
    public async Task Seeding_again_in_development_moves_untouched_placeholders_to_files()
    {
        await Seeder().SeedDemo("pw", CancellationToken.None);
        await Seeder().SeedDemo("pw", CancellationToken.None, filePrinters: true);

        (await _db.NewContext().Printers.ToListAsync()).Should().OnlyContain(p => p.ConnectionMode == PrinterConnection.File);
    }

    [Fact]
    public async Task Seeded_users_record_their_pin_length_and_max_length_is_just_a_number()
    {
        var users = await Seeder().SeedDemo("pw", CancellationToken.None);

        users.Should().OnlyContain(u => u.Pin.Length == 4);
        var session = new FakeSession { Terminal = new Application.Security.DeviceTerminal(Guid.NewGuid(), "Counter A") };
        (await new GetMaxPinLength(_db.Context, session).Execute(CancellationToken.None)).Should().Be(4);
    }

    [Fact]
    public async Task Require_pin_change_flags_the_user()
    {
        await Seeder().SeedDemo("pw", CancellationToken.None);

        (await Seeder().RequirePinChange("edna", CancellationToken.None)).Should().BeTrue();
        (await Seeder().RequirePinChange("nobody", CancellationToken.None)).Should().BeFalse();
        (await _db.NewContext().Users.SingleAsync(u => u.Name == "edna")).MustChangePin.Should().BeTrue();
    }
}
