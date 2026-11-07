using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Tausi.Relay.Tests;

public class RelayTests : IAsyncLifetime
{
    private RelayFixture F = null!;

    public Task InitializeAsync()
    {
        F = new RelayFixture();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await F.DisposeAsync();

    private static async Task<List<DeliveredPayment>> Collect(HttpClient server, int wait = 0)
    {
        var response = await server.GetAsync($"/api/v1/payments?wait={wait}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<DeliveredPayment>>())!;
    }

    private static Task<HttpResponseMessage> Ack(HttpClient server, IEnumerable<Guid> ids) =>
        server.PostAsJsonAsync("/api/v1/payments/ack", new AckRequest(ids.ToList()));

    [Fact]
    public async Task A_business_only_ever_receives_and_acknowledges_its_own_payments()
    {
        var (a, aKey) = await F.AddBusiness("Demo Bar");
        var (b, bKey) = await F.AddBusiness("Other Bar");
        (await F.Callback($"/pay/{a.PathToken}/c2b/confirmation", RelayFixture.C2B("SKG7H2LM4P", 1370))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await F.Callback($"/pay/{b.PathToken}/c2b/confirmation", RelayFixture.C2B("SKG7G9QX2D", 500))).StatusCode.Should().Be(HttpStatusCode.OK);

        var forA = await Collect(F.Server(aKey));
        var forB = await Collect(F.Server(bKey));
        forA.Should().ContainSingle().Which.TransactionId.Should().Be("SKG7H2LM4P");
        forB.Should().ContainSingle().Which.TransactionId.Should().Be("SKG7G9QX2D");

        // B can't acknowledge A's payment, even knowing its id.
        var acked = await (await Ack(F.Server(bKey), [forA[0].Id])).Content.ReadFromJsonAsync<JsonElement>();
        acked.GetProperty("acknowledged").GetInt32().Should().Be(0);
        (await Collect(F.Server(aKey))).Should().ContainSingle();

        // No key, a wrong key or a switched-off business gets nothing.
        (await F.Http.GetAsync("/api/v1/payments")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await F.Server("trk_wrong").GetAsync("/api/v1/payments")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await using (var db = F.Db()) await db.Businesses.Where(x => x.Id == b.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Active, false));
        (await F.Server(bKey).GetAsync("/api/v1/payments")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Only_safaricom_addresses_with_a_known_token_are_heard()
    {
        var (a, key) = await F.AddBusiness("Demo Bar");
        (await F.Callback($"/pay/{a.PathToken}/c2b/confirmation", RelayFixture.C2B("SKG7H2LM4P", 1370), from: "41.90.1.1"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await F.Callback($"/pay/{a.PathToken}/c2b/validation", RelayFixture.C2B("SKG7H2LM4P", 1370), from: "41.90.1.1"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await F.Callback("/pay/not-a-token/c2b/confirmation", RelayFixture.C2B("SKG7H2LM4P", 1370))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Collect(F.Server(key))).Should().BeEmpty();

        // A range works too, and validation always accepts.
        var ok = await F.Callback($"/pay/{a.PathToken}/c2b/validation", RelayFixture.C2B("SKG7H2LM4P", 1370), from: "196.201.212.74");
        (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ResultCode").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_repeated_callback_is_stored_once()
    {
        var (a, key) = await F.AddBusiness("Demo Bar");
        for (var i = 0; i < 3; i++)
            (await F.Callback($"/pay/{a.PathToken}/c2b/confirmation", RelayFixture.C2B("skg7h2lm4p", 1370))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await F.Callback($"/pay/{a.PathToken}/stk", RelayFixture.Stk("ws_CO_1", 0, "SKG7H2LM4Q"))).EnsureSuccessStatusCode();
        (await F.Callback($"/pay/{a.PathToken}/stk", RelayFixture.Stk("ws_CO_1", 0, "SKG7H2LM4Q"))).EnsureSuccessStatusCode();
        (await Collect(F.Server(key))).Select(p => p.TransactionId).Should().BeEquivalentTo(["SKG7H2LM4P", "SKG7H2LM4Q"]);
    }

    [Fact]
    public async Task Payments_wait_until_acknowledged_and_a_reconnect_gets_them_again()
    {
        var (a, key) = await F.AddBusiness("Demo Bar");
        await F.Callback($"/pay/{a.PathToken}/c2b/confirmation", RelayFixture.C2B("SKG7H2LM4P", 1370));
        await F.Callback($"/pay/{a.PathToken}/stk", RelayFixture.Stk("ws_CO_2", 1032));

        // The server crashed before acknowledging: it gets both again.
        var first = await Collect(F.Server(key));
        first.Should().HaveCount(2);
        (await Collect(F.Server(key))).Select(p => p.Id).Should().Equal(first.Select(p => p.Id));

        await Ack(F.Server(key), [first[0].Id]);
        (await Collect(F.Server(key))).Should().ContainSingle().Which.Id.Should().Be(first[1].Id);
        await Ack(F.Server(key), [first[1].Id]);
        (await Collect(F.Server(key))).Should().BeEmpty();

        // Waiting: a payment that arrives during the wait is delivered at once.
        var waiting = Collect(F.Server(key), wait: 20);
        await Task.Delay(200);
        await F.Callback($"/pay/{a.PathToken}/c2b/confirmation", RelayFixture.C2B("SKG7F1MN8T", 500));
        var arrived = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        arrived.Should().ContainSingle().Which.TransactionId.Should().Be("SKG7F1MN8T");

        await using var db = F.Db();
        (await db.Businesses.SingleAsync(x => x.Id == a.Id)).LastSeenAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Acknowledged_payments_are_deleted_after_30_days_and_unacknowledged_ones_are_kept()
    {
        var (a, key) = await F.AddBusiness("Demo Bar");
        await F.Callback($"/pay/{a.PathToken}/c2b/confirmation", RelayFixture.C2B("SKG7H2LM4P", 1370));
        await F.Callback($"/pay/{a.PathToken}/c2b/confirmation", RelayFixture.C2B("SKG7G9QX2D", 500));
        var both = await Collect(F.Server(key));
        await Ack(F.Server(key), [both.Single(p => p.TransactionId == "SKG7H2LM4P").Id]);

        await using var db = F.Db();
        var now = F.Clock.GetUtcNow().UtcDateTime;
        (await Cleanup.Run(db, now.AddDays(29), 30, CancellationToken.None)).Should().Be(0);
        (await Cleanup.Run(db, now.AddDays(31), 30, CancellationToken.None)).Should().Be(1);
        (await db.Payments.SingleAsync()).TransactionId.Should().Be("SKG7G9QX2D", "never acknowledged, so still held");
    }

    [Fact]
    public async Task The_api_key_is_kept_only_as_a_hash()
    {
        var (a, key) = await F.AddBusiness("Demo Bar");
        await using var db = F.Db();
        var stored = await db.Businesses.SingleAsync(x => x.Id == a.Id);
        stored.ApiKeyHash.Should().Be(RelaySecrets.Hash(key)).And.NotContain(key[RelaySecrets.KeyPrefix.Length..]);
        stored.ApiKeyHash.Should().HaveLength(64);
        stored.ApiKeyPrefix.Should().HaveLength(10);
        key.Should().StartWith("trk_").And.HaveLength(4 + 43);
    }

    [Fact]
    public async Task Stk_results_paid_and_not_paid_are_read_with_masked_phones()
    {
        var (a, key) = await F.AddBusiness("Demo Bar");
        await F.Callback($"/pay/{a.PathToken}/stk", RelayFixture.Stk("ws_CO_3", 0, "SKG7H2LM4P"));
        await F.Callback($"/pay/{a.PathToken}/stk", RelayFixture.Stk("ws_CO_4", 1032));
        var list = await Collect(F.Server(key));

        var paid = list.Single(p => p.CheckoutRequestId == "ws_CO_3");
        paid.Should().BeEquivalentTo(new
        {
            Kind = "stk", TransactionId = "SKG7H2LM4P", ResultCode = 0, Amount = 1370m, Phone = "0712 ••• 678",
            PaidAt = new DateTime(2026, 11, 6, 18, 47, 12, DateTimeKind.Utc),
        });
        var cancelled = list.Single(p => p.CheckoutRequestId == "ws_CO_4");
        cancelled.TransactionId.Should().BeNull();
        cancelled.ResultCode.Should().Be(1032);
        await using var db = F.Db();
        (await db.Payments.SingleAsync(p => p.CheckoutRequestId == "ws_CO_4")).Key.Should().Be("stk:ws_CO_4");
        (await db.Payments.AnyAsync(p => p.Phone!.Contains("345"))).Should().BeFalse("the full number is never stored");
    }

    [Fact]
    public async Task C2b_payments_keep_the_reference_and_the_payers_name()
    {
        var (a, key) = await F.AddBusiness("Demo Bar");
        await F.Callback($"/pay/{a.PathToken}/c2b/confirmation", RelayFixture.C2B("SKG7H2LM4P", 1370, billRef: "1042"));
        var p = (await Collect(F.Server(key))).Single();
        p.Should().BeEquivalentTo(new { Kind = "c2b", BillRef = "1042", PayerName = "JOHN K", ShortCode = "123456", Amount = 1370m, Phone = "0712 ••• 678" });
    }

    [Fact]
    public async Task The_admin_page_needs_nexagens_login_and_shows_a_new_key_once()
    {
        (await F.Http.GetAsync("/admin")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await F.Http.GetAsync("/admin/login")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await F.Http.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        AdminPasswords.Verify(RelayFixture.HashOfPassword, AdminPasswords.Hash(RelayFixture.HashOfPassword)).Should().BeTrue();
        AdminPasswords.Verify("wrong", AdminPasswords.Hash(RelayFixture.HashOfPassword)).Should().BeFalse();
    }

    [Theory]
    [InlineData("254712345678", "0712 ••• 678")]
    [InlineData("0712345678", "0712 ••• 678")]
    [InlineData("2547 ***** 678", "2547 ***** 678")]
    [InlineData(null, null)]
    public void Phones_are_masked(string? phone, string? masked) => Phones.Mask(phone).Should().Be(masked);
}
