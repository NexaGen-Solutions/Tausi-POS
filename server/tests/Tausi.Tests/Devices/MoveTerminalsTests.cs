using FluentAssertions;
using Tausi.Application.Auditing;
using Tausi.Application.Events;
using Tausi.Application.Terminals;
using Tausi.Domain.Common;
using Tausi.Tests.Tickets;

namespace Tausi.Tests.Devices;

/// <summary>Moving to a new server PC: one action sends the terminals to the new address, if it holds this business.</summary>
public class MoveTerminalsTests : ApprovalsFixtureBase
{
    private Task<string> Move(string address) => F.Get<MoveTerminals>().Execute(new MoveTerminalsRequest(address), None);

    [Fact]
    public async Task Terminals_move_to_a_new_server_that_holds_this_business()
    {
        await F.LoginOn("Administrator", "Counter B");
        F.Servers.Servers["http://192.168.1.20:5080"] = "test";
        F.Events.Published.Clear();

        (await Move(" http://192.168.1.20:5080/admin ")).Should().Be("http://192.168.1.20:5080");
        F.Events.Published.Should().ContainSingle(e => e.Name == PosEventNames.TerminalsMove);
        F.Read().AuditEntries.Should().Contain(a => a.Action == AuditActions.TerminalsMoved && a.EntityId == "http://192.168.1.20:5080");
    }

    [Fact]
    public async Task Not_to_a_server_that_doesnt_answer_or_holds_another_business()
    {
        await F.LoginOn("Administrator", "Counter B");
        F.Servers.Servers["http://192.168.1.30:5080"] = "Another Bar";
        await FluentActions.Awaiting(() => Move("http://192.168.1.20:5080")).Should().ThrowAsync<DomainException>().Where(e => e.Code == "move_unreachable");
        await FluentActions.Awaiting(() => Move("http://192.168.1.30:5080")).Should().ThrowAsync<DomainException>().Where(e => e.Code == "move_other_business");
        await FluentActions.Awaiting(() => Move("ftp://x")).Should().ThrowAsync<Application.Common.FieldErrorsException>();
        F.Events.Published.Should().NotContain(e => e.Name == PosEventNames.TerminalsMove);
    }

    [Fact]
    public async Task Only_device_managers_move_terminals()
    {
        F.Servers.Servers["http://192.168.1.20:5080"] = "test";
        await FluentActions.Awaiting(() => Move("http://192.168.1.20:5080")).Should().ThrowAsync<Application.Security.PermissionDeniedException>();
    }
}
