using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Settings;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>
/// M-Pesa over HTTP with the real relay (in memory) and a fake Daraja: Settings → M-Pesa and Test
/// connection, a QR payment that Safaricom sends to the relay landing on its bill, the M-Pesa ledger,
/// and that secrets never come back from the API.
/// </summary>
[Collection(MpesaCollection.Name)]
public class MpesaApiTests(MpesaApp app) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (app.Db(db => db.WorkPeriods.Any(w => w.IsOpen))) return;
        var supervisor = await app.PairTerminal();
        await supervisor.LoginWithPin(app.Pins["SAM"]);
        (await supervisor.Http.PostAsync("/api/day/open", null)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    /// <summary>Buy Goods till 123456, store 654321, the credentials and a relay business.</summary>
    private async Task<(HttpClient Admin, string Token)> SetUp()
    {
        app.Db(db =>
        {
            var row = db.BusinessSettings.Single();
            SettingsRegistry.Write(row, SettingsRegistry.MpesaTill, JsonValue.Create("123456"));
            SettingsRegistry.Write(row, SettingsRegistry.MpesaStore, JsonValue.Create("654321"));
            SettingsRegistry.Write(row, SettingsRegistry.MpesaRelayUrl, JsonValue.Create("https://pay.example.test"));
            return db.SaveChanges();
        });
        var (key, token) = await app.RelayBusiness();
        var admin = await app.AdminClient();
        await Ok(await admin.PutAsJsonAsync("/api/admin/mpesa/secrets", new { consumerKey = "ck-123", consumerSecret = "cs-456", passkey = "pk-789", relayApiKey = key }));
        return (admin, token);
    }

    [Fact]
    public async Task Test_connection_registers_the_relay_and_a_qr_payment_lands_on_its_bill()
    {
        var (admin, token) = await SetUp();
        var checks = await Ok(await admin.PostAsync("/api/admin/mpesa/test", null));
        checks.EnumerateArray().Select(c => c.GetProperty("ok").GetBoolean()).Should().OnlyContain(ok => ok, checks.ToString());
        app.Daraja.Registrations.Should().Contain(("654321", $"https://pay.example.test/pay/{token}/c2b/confirmation"));

        // edna's ticket; the customer scans the bill's QR, so the reference is its number.
        var edna = await app.PairTerminal();
        (await edna.LoginWithPin(app.Pins["edna"])).EnsureSuccessStatusCode();
        var punch = app.Db(db => db.MenuItems.Single(i => i.Name == "Pineapple Punch").Id);
        var ticket = await Ok(await edna.Http.PostAsJsonAsync("/api/pos/tickets",
            new { customerName = "M-Pesa", lines = new[] { new { menuItemId = punch, quantity = 2 } } }));
        var (id, number) = (ticket.GetProperty("id").GetGuid(), ticket.GetProperty("number").GetInt64());
        await app.Safaricom($"/pay/{token}/c2b/confirmation", new
        {
            TransactionType = "Buy Goods", TransID = "SKG7H2LM4P", TransTime = "20261106214600", TransAmount = "100", BusinessShortCode = "654321",
            BillRefNumber = number.ToString(), MSISDN = "254712345678", FirstName = "JOHN", LastName = "K",
        });

        (await Ok(await admin.PostAsync("/api/admin/mpesa/fetch", null))).GetProperty("saved").GetInt32().Should().Be(1);
        app.Db(db => db.Payments.Where(p => p.TicketId == id).Select(p => new { p.Amount, p.MpesaCode, Status = p.Status.ToString() }).ToList())
            .Should().ContainSingle().Which.Should().Be(new { Amount = 100m, MpesaCode = (string?)"SKG7H2LM4P", Status = "Pending" });

        var ledger = await Ok(await admin.PostAsJsonAsync("/api/admin/mpesa/ledger", new { quick = "Today" }));
        var row = ledger.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("code").GetString() == "SKG7H2LM4P");
        row.GetProperty("how").GetString().Should().Be("QR on bill");
        (await Ok(await admin.PostAsync("/api/admin/mpesa/fetch", null))).GetProperty("saved").GetInt32().Should().Be(0, "acknowledged, so not delivered again");
        (await admin.PostAsJsonAsync("/api/admin/mpesa/ledger/export", new { quick = "Today" })).Content.Headers.ContentType!.MediaType
            .Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    [Fact]
    public async Task Secrets_are_never_returned_and_the_screens_need_their_permissions()
    {
        var (admin, _) = await SetUp();
        var text = await (await admin.GetAsync("/api/admin/mpesa")).Content.ReadAsStringAsync()
                   + await (await admin.GetAsync("/api/admin/settings")).Content.ReadAsStringAsync();
        foreach (var secret in new[] { "ck-123", "cs-456", "pk-789", "trk_" }) text.Should().NotContain(secret);
        app.Db(db => db.MpesaAccounts.Single().ConsumerSecret).Should().NotContain("cs-456", "stored encrypted");

        var waiter = await app.PairTerminal();
        (await waiter.LoginWithPin(app.Pins["edna"])).EnsureSuccessStatusCode();
        (await waiter.Http.GetAsync("/api/admin/mpesa")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await waiter.Http.PostAsJsonAsync("/api/admin/mpesa/ledger", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var pos = await Ok(await waiter.Http.GetAsync("/api/pos/mpesa"));
        pos.GetProperty("setUp").GetBoolean().Should().BeTrue();
        pos.GetProperty("waiterPrompts").GetBoolean().Should().BeTrue();
    }
}
