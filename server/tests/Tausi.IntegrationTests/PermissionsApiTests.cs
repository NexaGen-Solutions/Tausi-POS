using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Tausi.Application.Auditing;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>The permission grid and roles over HTTP (PostgreSQL).</summary>
[Collection(AppCollection.Name)]
public class PermissionsApiTests(TausiApp app)
{
    private Guid Role(string name) => app.Db(db => db.Roles.Single(r => r.Name == name).Id);

    private static async Task<HttpResponseMessage> Save(HttpClient admin, params object[] changes)
    {
        var grid = await (await admin.GetAsync("/api/admin/permissions")).Json();
        return await admin.PutAsJsonAsync("/api/admin/permissions", new { version = grid.GetProperty("version").GetString(), changes });
    }

    [Fact]
    public async Task The_grid_has_every_key_and_role_and_refuses_what_would_lock_people_out()
    {
        var admin = await app.AdminClient();
        var grid = await (await admin.GetAsync("/api/admin/permissions")).Json();
        grid.GetProperty("keys").GetArrayLength().Should().Be(27);
        grid.GetProperty("roles").EnumerateArray().Take(4).Select(r => r.GetProperty("name").GetString())
            .Should().Equal("Waiter", "Cashier", "Supervisor", "Admin");
        grid.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("key").GetString() == "ticket.settle")
            .GetProperty("approvable").GetBoolean().Should().BeFalse();

        var settle = await Save(admin, new { roleId = Role("Waiter"), permissionKey = "ticket.settle", value = "NeedsApproval" });
        settle.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await settle.Json()).GetProperty("detail").GetString().Should().Contain("can't be approved");

        var locked = await Save(admin, new { roleId = Role("Admin"), permissionKey = "permissions.manage", value = "NotAllowed" });
        (await locked.Json()).GetProperty("detail").GetString().Should().Be("Kept so you can't lock yourself out.");

        var roles = grid.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
        object[] Everyone(string key, string value) => roles.Select(id => (object)new { roleId = id, permissionKey = key, value }).ToArray();

        var nobodyStaff = await Save(admin, Everyone("staff.manage", "NotAllowed"));
        nobodyStaff.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await nobodyStaff.Json()).GetProperty("fieldErrors").GetProperty("staff.manage").GetString().Should().Contain("Nobody active");

        var allVoidsNeedApproval = await Save(admin, Everyone("order.void", "NeedsApproval"));
        (await allVoidsNeedApproval.Json()).GetProperty("fieldErrors").GetProperty("order.void").GetString()
            .Should().Be("Nobody can approve \"Void a printed item\". Allow it for at least one role.");

        var stale = await admin.PutAsJsonAsync("/api/admin/permissions", new
        {
            version = "0000000000000000",
            changes = new[] { new { roleId = Role("Waiter"), permissionKey = "order.gift", value = "NeedsApproval" } },
        });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_saved_cell_is_audited_announced_and_used_on_the_next_request()
    {
        var admin = await app.AdminClient();
        var edna = await app.PairTerminal();
        await edna.LoginWithPin(app.Pins["edna"]);
        (await (await edna.Http.GetAsync("/api/auth/me")).Json()).GetProperty("permissions").GetProperty("order.gift").GetString()
            .Should().Be("NotAllowed");

        var changed = new TaskCompletionSource();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, $"/hubs/pos?device_token={edna.DeviceToken}"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult(edna.SessionToken);
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();
        hub.On<JsonElement>("permissions.changed", _ => changed.TrySetResult());
        await hub.StartAsync();
        var audits = app.Db(db => db.AuditEntries.Count(a => a.Action == AuditActions.PermissionChanged));

        var saved = await Save(admin,
            new { roleId = Role("Waiter"), permissionKey = "order.gift", value = "NeedsApproval" },
            new { roleId = Role("Waiter"), permissionKey = "ticket.reprint", value = "Allowed" });
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        try
        {
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            app.Db(db => db.AuditEntries.Count(a => a.Action == AuditActions.PermissionChanged)).Should().Be(audits + 2);
            (await (await edna.Http.GetAsync("/api/auth/me")).Json()).GetProperty("permissions").GetProperty("order.gift").GetString()
                .Should().Be("NeedsApproval", "the same session sees the change without logging out");

            var history = await (await admin.GetAsync("/api/admin/permissions/history")).Json();
            history.EnumerateArray().Should().Contain(h => h.GetProperty("key").GetString() == "order.gift"
                && h.GetProperty("old").GetString() == "NotAllowed" && h.GetProperty("new").GetString() == "NeedsApproval");
        }
        finally
        {
            (await Save(admin,
                new { roleId = Role("Waiter"), permissionKey = "order.gift", value = "NotAllowed" },
                new { roleId = Role("Waiter"), permissionKey = "ticket.reprint", value = "NeedsApproval" })).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task A_role_copied_from_waiter_can_be_given_voids_and_its_user_approves_them()
    {
        var admin = await app.AdminClient();
        var created = await admin.PostAsJsonAsync("/api/admin/roles", new { name = "Bar captain", copyFromRoleId = Role("Waiter") });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var captain = Role("Bar captain");
        var grid = await created.Json();
        grid.GetProperty("cells").GetProperty(captain.ToString()).GetProperty("order.void").GetString().Should().Be("NeedsApproval");
        (await admin.PostAsJsonAsync("/api/admin/roles", new { name = "bar CAPTAIN" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await admin.SetPermission(captain, "order.void", "Allowed");
        (await admin.PostAsJsonAsync("/api/admin/staff", new { name = "Otieno", roleId = captain, startingPin = "4826" })).EnsureSuccessStatusCode();
        var waiter = await app.PairTerminal();
        await waiter.LoginWithPin(app.Pins["edna"]);
        var approve = await waiter.Http.PostAsJsonAsync("/api/auth/approve", new { pin = "4826", permissionKey = "order.void" });
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());

        var inUse = await admin.DeleteAsync($"/api/admin/roles/{captain}");
        inUse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await inUse.Json()).GetProperty("detail").GetString().Should().StartWith("Otieno has this role.");
        (await admin.DeleteAsync($"/api/admin/roles/{Role("Waiter")}")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var spare = await (await admin.PostAsJsonAsync("/api/admin/roles", new { name = "Spare" })).Json();
        var spareId = Role("Spare");
        spare.GetProperty("cells").GetProperty(spareId.ToString()).EnumerateObject().Should().OnlyContain(c => c.Value.GetString() == "NotAllowed");
        var rowVersion = spare.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Spare").GetProperty("rowVersion").GetInt64();
        (await admin.PutAsJsonAsync($"/api/admin/roles/{spareId}", new { name = "Spare role", rowVersion })).EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"/api/admin/roles/{spareId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        app.Db(db => db.Roles.Any(r => r.Id == spareId)).Should().BeFalse();
    }
}
