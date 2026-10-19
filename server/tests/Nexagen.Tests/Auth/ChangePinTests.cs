using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Nexagen.Application.Auditing;
using Nexagen.Application.Auth;
using Nexagen.Domain.Common;
using Nexagen.Domain.People;
using Nexagen.Tests.Support;

namespace Nexagen.Tests.Auth;

public class ChangePinTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private readonly FastPinHasher _hasher = new();
    private readonly FakeSession _session = new();
    private User _edna = null!;
    private ChangePin _change = null!;

    public async Task InitializeAsync()
    {
        var roles = await Builders.SeedRoles(_db.Context);
        _edna = await Builders.AddUser(_db.Context, _hasher, "edna", roles["Waiter"], "4821");
        _edna.MustChangePin = true;
        await Builders.AddUser(_db.Context, _hasher, "CAROL", roles["Cashier"], "5739");
        await Builders.AddUser(_db.Context, _hasher, "gone", roles["Waiter"], "8642", active: false);
        await _db.Context.SaveChangesAsync();
        _session.User = Builders.Session(_edna, roles["Waiter"]);
        _change = new ChangePin(_db.Context, _session, _hasher, new AuditLog(_db.Context, _session, new FakeTimeProvider()));
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private Task Change(string pin) => _change.Execute(new ChangePinRequest(pin), CancellationToken.None);

    private async Task Rejected(string pin, string code) =>
        (await FluentActions.Awaiting(() => Change(pin)).Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(code);

    [Fact]
    public async Task Success_stores_the_new_pin_clears_must_change_and_is_audited()
    {
        await Change("90517");

        var user = await _db.NewContext().Users.SingleAsync(u => u.Id == _edna.Id);
        user.MustChangePin.Should().BeFalse();
        user.PinLength.Should().Be(5);
        _hasher.Verify("90517", user.PinHash).Should().BeTrue();
        user.PinLookup.Should().Be(_hasher.Lookup("90517"));

        var audit = await _db.NewContext().AuditEntries.SingleAsync(a => a.Action == AuditActions.PinChanged);
        audit.NewValueJson.Should().NotContain("90517", "PINs never go into the audit log");
    }

    [Fact]
    public async Task A_pin_used_by_another_active_user_is_refused_without_saying_who()
    {
        var ex = await FluentActions.Awaiting(() => Change("5739")).Should().ThrowAsync<DomainException>();
        ex.Which.Code.Should().Be(PinChangeCodes.Taken);
        ex.Which.Message.Should().Be("Choose a different PIN.");
    }

    [Fact]
    public async Task A_pin_of_an_inactive_user_may_be_reused()
    {
        await Change("8642");
    }

    [Theory]
    [InlineData("0000")]
    [InlineData("1234")]
    [InlineData("4321")]
    [InlineData("777777")]
    [InlineData("23456")]
    public async Task Easy_sequences_are_refused(string pin) => await Rejected(pin, PinChangeCodes.TooSimple);

    [Fact]
    public async Task The_same_pin_as_now_is_refused() => await Rejected("4821", PinChangeCodes.Unchanged);

    [Theory]
    [InlineData("123")]
    [InlineData("1234567")]
    [InlineData("12a4")]
    public async Task Malformed_pins_are_refused(string pin) => await Rejected(pin, PinChangeCodes.Invalid);

    [Fact]
    public async Task Nothing_changes_when_refused()
    {
        await FluentActions.Awaiting(() => Change("1111")).Should().ThrowAsync<DomainException>();
        (await _db.NewContext().Users.SingleAsync(u => u.Id == _edna.Id)).MustChangePin.Should().BeTrue();
    }
}

public class PinRulesTests
{
    [Theory]
    [InlineData("0000", true)]
    [InlineData("9999", true)]
    [InlineData("1234", true)]
    [InlineData("0123", true)]
    [InlineData("3456789", true)]
    [InlineData("9876", true)]
    [InlineData("6543", true)]
    [InlineData("1235", false)]
    [InlineData("4821", false)]
    [InlineData("1122", false)]
    [InlineData("9012", false)]
    public void Too_simple(string pin, bool expected) => Pin.IsTooSimple(pin).Should().Be(expected);
}
