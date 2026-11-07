using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Mpesa;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Settling;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Domain.Mpesa;
using Tausi.Domain.People;
using Tausi.Domain.Service;
using Tausi.Infrastructure.Mpesa;
using Tausi.Tests.Tickets;

namespace Tausi.Tests.Mpesa;

/// <summary>
/// M-Pesa (Task 18) with a fake Daraja and a fake relay: settings and secrets, STK prompts by both
/// routes, the QR on bills, payments received and typed, the M-Pesa ledger and collection.
/// </summary>
public class MpesaTests : ApprovalsFixtureBase
{
    private const string Stk = "https://pay.example.test/pay/tok123/stk";

    /// <summary>Buy Goods till 123456 (store 654321), sandbox, with credentials and the relay set up.</summary>
    private async Task SetUp(bool relay = true)
    {
        await F.SetSetting(SettingsRegistry.MpesaTill, JsonValue.Create("123456"));
        await F.SetSetting(SettingsRegistry.MpesaStore, JsonValue.Create("654321"));
        var secrets = new FakeSecrets();
        F.Db.MpesaAccounts.Add(new MpesaAccount
        {
            ConsumerKey = secrets.Protect("consumer-key"), ConsumerSecret = secrets.Protect("consumer-secret"), Passkey = secrets.Protect("passkey"),
            RelayApiKey = relay ? secrets.Protect("trk_test") : null, StkCallbackUrl = Stk,
        });
        await F.Db.SaveChangesAsync();
        F.Db.ChangeTracker.Clear();
    }

    /// <summary>Table 7 for KSh 1,370 by edna, opened by CAROL on Counter A with her shift started.</summary>
    private async Task<TicketDetailDto> Table7ForCarol(string table = "7")
    {
        await F.Db.MenuItems.Where(i => i.Name == "House Water 1L")
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.VatExempt, true).SetProperty(i => i.VatRate, 0m));
        await F.LoginOn("edna", "Counter A");
        var ticket = await Printed(table, ("Tusker Lager", 2), ("Guiness", 1), ("House Water 1L", 1), ("Sodas 300ml", 2), ("Pineapple Punch", 1));
        ticket.Total.Should().Be(1370m);
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        if ((await F.Get<Tausi.Application.Cash.ShiftQueries>().Current(None)).Shift is null) await StartShift();
        Later();
        return ticket;
    }

    private Task<PromptDto> Send(Guid ticket, string phone = "0712 345 678", decimal? amount = null, bool replace = false, bool fromOrder = false) =>
        F.Get<MpesaPrompts>().Send(ticket, new SendPromptRequest(phone, amount, replace, fromOrder), None);

    private Task Receive(RelayPaymentDto p) => F.Get<MpesaInbox>().Receive(p, None);

    private MpesaTransaction Transaction(string code) => F.Read().MpesaTransactions.Single(t => t.TransactionId == code);

    private List<Payment> Payments(Guid ticket) => F.Read().Payments.Where(p => p.TicketId == ticket && p.Status != PaymentStatus.Removed).ToList();

    // ---- Settings ----

    [Fact]
    public async Task Credentials_are_stored_encrypted_and_never_returned_by_any_api()
    {
        await F.LoginOn("Administrator", "Counter B");
        var admin = F.Get<MpesaAdmin>();
        var saved = await admin.SaveSecrets(new SaveMpesaSecretsRequest("my-consumer-key", "my-secret", "my-passkey", "trk_relay_key"), None);

        saved.Secrets.Should().Be(new MpesaSecretsDto(true, true, true, true, false));
        var stored = F.Read().MpesaAccounts.Single();
        stored.ConsumerSecret.Should().StartWith("enc:").And.NotContain("my-secret");
        var everything = JsonSerializer.Serialize(new object[] { saved, await admin.Get(None), await F.Get<GetSettings>().Execute(None) });
        foreach (var secret in new[] { "my-consumer-key", "my-secret", "my-passkey", "trk_relay_key" }) everything.Should().NotContain(secret);

        // The audit says which changed, never the values.
        F.Read().AuditEntries.Where(a => a.Action == "mpesa.settings_changed").ToList().Should().ContainSingle()
            .Which.NewValueJson.Should().Contain("consumerSecret").And.NotContain("my-secret");

        // null leaves a secret as it is; "" clears it.
        (await admin.SaveSecrets(new SaveMpesaSecretsRequest(null, null, "", null), None)).Secrets.Passkey.Should().BeFalse();
        F.Read().MpesaAccounts.Single().ConsumerSecret.Should().Be(stored.ConsumerSecret);

        await F.LoginOn("CAROL", "Counter A");
        await FluentActions.Awaiting(() => admin.Get(None)).Should().ThrowAsync<PermissionDeniedException>();
    }

    [Fact]
    public async Task Test_connection_checks_daraja_then_the_relay_and_registers_the_relay_paths_once()
    {
        await SetUp();
        await F.LoginOn("Administrator", "Counter B");
        var checks = await F.Get<MpesaAdmin>().Test(None);
        checks.Should().OnlyContain(c => c.Ok);
        F.Daraja.Registrations.Should().ContainSingle().Which.Should().Be(("654321",
            "https://pay.example.test/pay/tok123/c2b/confirmation", "https://pay.example.test/pay/tok123/c2b/validation"));
        F.Read().MpesaAccounts.Single().StkCallbackUrl.Should().Be(Stk);

        (await F.Get<MpesaAdmin>().Test(None)).Should().OnlyContain(c => c.Ok);
        F.Daraja.Registrations.Should().ContainSingle("already registered for the same addresses");

        F.Daraja.WrongCredentials = true;
        (await F.Get<MpesaAdmin>().Test(None)).Should().ContainSingle()
            .Which.Should().Be(new MpesaCheck("Daraja", false, "Safaricom didn't accept the consumer key and secret."));
        F.Daraja.WrongCredentials = false;
        F.Relay.Down = true;
        var down = await F.Get<MpesaAdmin>().Test(None);
        down.Single(c => c.Name == "Relay").Ok.Should().BeFalse();
        down.Single(c => c.Name == "Relay").Message.Should().StartWith("Can't reach the relay");
    }

    [Fact]
    public async Task Without_settings_nothing_mpesa_happens_and_typed_codes_work_as_before()
    {
        var ticket = await Table7ForCarol();
        (await F.Get<MpesaAdmin>().ForPos(F.Get<MpesaReceived>(), None)).Should().Be(new PosMpesaDto(false, false, false, true, 0));
        await FluentActions.Awaiting(() => Send(ticket.Id)).Should().ThrowAsync<DomainException>().Where(e => e.Code == MpesaCodes.NotSetUp);
        F.Daraja.Calls.Should().BeEmpty();
        Jobs(ticket.Id).Should().OnlyContain(j => !j.TextPreview.Contains("M-PESA"));
    }

    // ---- STK ----

    [Theory]
    [InlineData("0712 345 678", "254712345678")]
    [InlineData("0112345678", "254112345678")]
    [InlineData("+254 712 345 678", "254712345678")]
    [InlineData("254712345678", "254712345678")]
    [InlineData("0812345678", null)]
    [InlineData("12345", null)]
    public void Phone_numbers_become_2547_or_2541(string phone, string? expected) => MpesaPhones.Normalise(phone).Should().Be(expected);

    [Fact]
    public async Task A_prompt_is_for_the_remaining_amount_at_most_and_one_waits_per_ticket()
    {
        await SetUp();
        var ticket = await Table7ForCarol();
        await FluentActions.Awaiting(() => Send(ticket.Id, amount: 1371)).Should().ThrowAsync<FieldErrorsException>();
        await FluentActions.Awaiting(() => Send(ticket.Id, amount: 100.5m)).Should().ThrowAsync<FieldErrorsException>();
        await FluentActions.Awaiting(() => Send(ticket.Id, phone: "0812")).Should().ThrowAsync<FieldErrorsException>();

        var first = await Send(ticket.Id);
        first.Should().Match<PromptDto>(p => p.Amount == 1370 && p.Phone == "0712 ••• 678" && p.Status == "Waiting");
        F.Daraja.Pushes.Single().Should().Be(new StkPushRequest("654321", "123456", false, "254712345678", 1370, Stk, ticket.Number.ToString(), $"Bill {ticket.Number}"));

        var again = await FluentActions.Awaiting(() => Send(ticket.Id, amount: 500)).Should().ThrowAsync<ConflictException>();
        again.Which.Code.Should().Be(MpesaCodes.PromptWaiting);
        var second = await Send(ticket.Id, amount: 500, replace: true);
        F.Read().MpesaPrompts.Single(p => p.Id == first.Id).Status.Should().Be(MpesaPromptStatus.Cancelled);
        second.Amount.Should().Be(500);
        F.Read().AuditEntries.Count(a => a.Action == "mpesa.prompt_sent").Should().Be(2);
    }

    [Fact]
    public async Task Paid_through_the_relay_adds_a_pending_payment_and_the_cashier_still_completes()
    {
        await SetUp();
        var ticket = await Table7ForCarol();
        var prompt = await Send(ticket.Id);
        await Receive(FakeRelay.Stk("ws_CO_1", 0, "SKG7H2LM4P", 1370));

        var status = await F.Get<MpesaPrompts>().Status(prompt.Id, None);
        status.Should().Match<PromptDto>(p => p.Status == "Paid" && p.Code == "SKG7H2LM4P" && p.BroughtBy == "edna");
        var payment = Payments(ticket.Id).Single();
        payment.Should().Match<Payment>(p => p.Status == PaymentStatus.Pending && p.Amount == 1370 && p.MpesaCode == "SKG7H2LM4P" && p.ShiftId == null);
        F.Read().Tickets.Single(k => k.Id == ticket.Id).Status.Should().Be(TicketStatus.Open, "only the cashier settles");
        Transaction("SKG7H2LM4P").Should().Match<MpesaTransaction>(t => t.Source == MpesaSource.Stk && t.Confirmed && t.Status == MpesaStatus.Applied);
        F.Events.Published.Select(p => p.Name).Should().Contain("mpesa.prompt");

        // The same answer again (the query, or a repeated callback) changes nothing.
        await Receive(FakeRelay.Stk("ws_CO_1", 0, "SKG7H2LM4P", 1370));
        Payments(ticket.Id).Should().ContainSingle();

        await F.Get<SettleTicket>().Execute(ticket.Id, None);
        var settled = F.Read().Payments.Single(p => p.TicketId == ticket.Id);
        settled.Status.Should().Be(PaymentStatus.Final);
        settled.TakenByUserId.Should().Be(F.User("CAROL").Id);
        settled.ShiftId.Should().NotBeNull();
    }

    [Fact]
    public async Task With_the_relay_down_the_query_route_completes_the_prompt_and_the_callback_later_adds_the_code()
    {
        await SetUp();
        var ticket = await Table7ForCarol();
        var prompt = await Send(ticket.Id);
        var prompts = F.Get<MpesaPrompts>();

        (await prompts.Status(prompt.Id, None)).Status.Should().Be("Waiting");
        F.Daraja.Calls.Should().NotContain("stkquery", "Daraja is asked at most every 5 seconds");
        F.Clock.Advance(TimeSpan.FromSeconds(6));
        (await prompts.Status(prompt.Id, None)).Status.Should().Be("Waiting", "the customer hasn't answered");
        F.Daraja.QueryAnswers["ws_CO_1"] = (0, "The service request is processed successfully.");
        F.Clock.Advance(TimeSpan.FromSeconds(6));
        var paid = await prompts.Status(prompt.Id, None);
        paid.Status.Should().Be("Paid");
        paid.Code.Should().BeNull("the query doesn't give the M-Pesa code");
        Payments(ticket.Id).Single().Should().Match<Payment>(p => p.Amount == 1370 && p.MpesaCode == null);

        // The relay comes back: the callback brings the code, and is not applied twice.
        await Receive(FakeRelay.Stk("ws_CO_1", 0, "SKG7H2LM4P", 1370));
        Payments(ticket.Id).Should().ContainSingle().Which.MpesaCode.Should().Be("SKG7H2LM4P");
        F.Read().MpesaTransactions.Should().ContainSingle().Which.TransactionId.Should().Be("SKG7H2LM4P");
    }

    [Theory]
    [InlineData(1032, "The customer cancelled the prompt.")]
    [InlineData(1, "The customer doesn't have enough money on M-Pesa.")]
    [InlineData(1037, "No response from the phone. It may be off or out of reach.")]
    [InlineData(2001, "Wrong PIN. Send the prompt again.")]
    [InlineData(4242, "M-Pesa didn't take the payment (4242: Something odd).")]
    public async Task Each_daraja_result_becomes_plain_words(int code, string message)
    {
        StkResults.Message(code, "Something odd").Should().Be(message);
        await SetUp();
        var ticket = await Table7ForCarol();
        var prompt = await Send(ticket.Id);
        await Receive(FakeRelay.Stk("ws_CO_1", code));
        var expected = StkResults.Message(code, "Cancelled");
        (await F.Get<MpesaPrompts>().Status(prompt.Id, None)).Should().Match<PromptDto>(p => p.Status == "Failed" && p.Message == expected);
        Payments(ticket.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task A_prompt_nobody_answers_expires_after_two_minutes()
    {
        await SetUp();
        var ticket = await Table7ForCarol();
        var prompt = await Send(ticket.Id);
        F.Clock.Advance(TimeSpan.FromMinutes(2));
        (await F.Get<MpesaPrompts>().Status(prompt.Id, None)).Status.Should().Be("Expired");
        (await Send(ticket.Id)).Status.Should().Be("Waiting", "an expired prompt doesn't block a new one");
    }

    [Fact]
    public async Task A_waiters_prompt_follows_the_setting_and_the_payment_is_brought_by_that_waiter()
    {
        await SetUp();
        var ticket = await Table7ForCarol();
        await F.LoginOn("edna", "Counter A");
        await FluentActions.Awaiting(() => Send(ticket.Id)).Should().ThrowAsync<PermissionDeniedException>("a waiter can't settle");

        await F.SetSetting(SettingsRegistry.MpesaWaiterPrompts, JsonValue.Create(false));
        await FluentActions.Awaiting(() => Send(ticket.Id, fromOrder: true)).Should().ThrowAsync<ForbiddenException>();
        (await F.Get<MpesaAdmin>().ForPos(F.Get<MpesaReceived>(), None)).WaiterPrompts.Should().BeFalse();

        await F.SetSetting(SettingsRegistry.MpesaWaiterPrompts, JsonValue.Create(true));
        var prompt = await Send(ticket.Id, fromOrder: true);
        prompt.Status.Should().Be("Waiting");
        await Receive(FakeRelay.Stk("ws_CO_1", 0, "SKG7H2LM4P", 1370));
        Payments(ticket.Id).Single().BroughtByUserId.Should().Be(F.User("edna").Id);
        F.Read().AuditEntries.Single(a => a.Action == "mpesa.prompt_sent").NewValueJson.Should().Contain("order screen").And.Contain("0712 \\u2022\\u2022\\u2022 678");
    }

    // ---- The QR on the bill ----

    [Fact]
    public async Task Bills_get_the_qr_when_it_is_on_and_not_when_it_is_off()
    {
        await SetUp();
        await F.LoginOn("edna", "Counter A");
        var on = await Printed("7", ("Tusker Lager", 2));
        var bill = Jobs(on.Id).Single(j => j.Kind == Tausi.Domain.Printing.PrintJobKind.Bill).TextPreview;
        bill.Should().Contain("SCAN TO PAY WITH M-PESA").And.Contain("[M-Pesa QR code]").And.Contain($"Till 123456 · KSh 500 · #{on.Number}");
        F.Daraja.Calls.Should().Contain("qr");

        await F.SetSetting(SettingsRegistry.MpesaQrOnBill, JsonValue.Create(false));
        var off = await Printed("8", ("Tusker Lager", 2));
        Jobs(off.Id).Single(j => j.Kind == Tausi.Domain.Printing.PrintJobKind.Bill).TextPreview.Should().NotContain("M-PESA").And.NotContain("Till 123456");
    }

    [Fact]
    public async Task A_slow_or_offline_daraja_never_holds_the_bill_up_more_than_three_seconds()
    {
        await SetUp();
        await F.LoginOn("edna", "Counter A");
        F.Daraja.QrDelay = TimeSpan.FromSeconds(30);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var slow = await Printed("7", ("Tusker Lager", 2));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
        var bill = Jobs(slow.Id).Single(j => j.Kind == Tausi.Domain.Printing.PrintJobKind.Bill);
        bill.TextPreview.Should().NotContain("[M-Pesa QR code]").And.Contain("Pay to Till 123456");
        var golden = bill.TextPreview.Replace(slow.Number.ToString(), "1001");
        Tausi.Tests.Printing.SlipsTests.Golden("bill-mpesa-no-qr-42", new Tausi.Application.Printing.PrintDocument([], golden));

        F.Daraja.QrDelay = TimeSpan.Zero;
        F.Daraja.Down = true;
        var offline = await Printed("8", ("Tusker Lager", 2));
        Jobs(offline.Id).Single(j => j.Kind == Tausi.Domain.Printing.PrintJobKind.Bill).TextPreview.Should().Contain("Pay to Till 123456");
    }

    // ---- Payments received ----

    [Fact]
    public async Task A_qr_payment_goes_on_its_bill_and_an_amount_alone_is_only_a_suggestion()
    {
        await SetUp();
        var ticket = await Table7ForCarol();
        var other = await Table7ForCarol("8");
        await F.Db.Tickets.Where(k => k.Id == ticket.Id).ExecuteUpdateAsync(u => u.SetProperty(k => k.BillPrintedAt, F.Clock.GetUtcNow()));

        // Its reference is ticket 8's number: added to it.
        await Receive(FakeRelay.C2B("SKG7H2LM4P", 1370, billRef: other.Number.ToString()));
        Payments(other.Id).Single().Should().Match<Payment>(p => p.Amount == 1370 && p.Status == PaymentStatus.Pending && p.BroughtByUserId == F.User("edna").Id);
        Transaction("SKG7H2LM4P").Status.Should().Be(MpesaStatus.Applied);

        // The same amount as table 7, no reference: only suggested.
        await Receive(FakeRelay.C2B("SKG7G9QX2D", 1370));
        Payments(ticket.Id).Should().BeEmpty("never applied on amount alone");
        var list = await F.Get<MpesaReceived>().For(ticket.Id, None);
        list.Should().ContainSingle().Which.Should().Match<ReceivedPaymentDto>(r => r.Code == "SKG7G9QX2D" && r.Match == "SameAmount");
        await Receive(FakeRelay.C2B("SKG7F1MN8T", 500));
        (await F.Get<MpesaReceived>().For(ticket.Id, None)).Select(r => r.Match).Should().Equal("SameAmount", "None");
    }

    [Fact]
    public async Task One_payment_pays_parts_of_two_bills_and_never_gives_change()
    {
        await SetUp();
        await Receive(FakeRelay.C2B("SKG7H2LM4P", 1500));
        var id = Transaction("SKG7H2LM4P").Id;
        var first = await Table7ForCarol();
        var paid = await F.Get<PaymentCommands>().Add(first.Id, new AddPaymentRequest(PaymentType.Mpesa, 1000, MpesaTransactionId: id), None);
        paid.Remaining.Should().Be(370);
        Transaction("SKG7H2LM4P").Should().Match<MpesaTransaction>(t => t.Status == MpesaStatus.PartlyApplied && t.AmountApplied == 1000);

        var second = await Table7ForCarol("8");
        var rest = await F.Get<PaymentCommands>().Add(second.Id, new AddPaymentRequest(PaymentType.Mpesa, null, MpesaTransactionId: id), None);
        rest.Payments!.Single().Should().Match<PaymentDto>(p => p.Amount == 500 && p.Change == null);
        rest.Remaining.Should().Be(870);
        Transaction("SKG7H2LM4P").Status.Should().Be(MpesaStatus.Applied);
        await FluentActions.Awaiting(() => F.Get<PaymentCommands>().Add(second.Id, new AddPaymentRequest(PaymentType.Mpesa, null, MpesaTransactionId: id), None))
            .Should().ThrowAsync<ConflictException>();

        // Taking it off a bill makes the money available again.
        await F.Get<PaymentCommands>().Remove(second.Id, rest.Payments!.Single().Id, None);
        Transaction("SKG7H2LM4P").Should().Match<MpesaTransaction>(t => t.Status == MpesaStatus.PartlyApplied && t.Available == 500);
    }

    [Fact]
    public async Task A_typed_code_is_confirmed_when_safaricoms_copy_arrives_and_flagged_after_24_hours_if_it_never_does()
    {
        await SetUp();
        var ticket = await Table7ForCarol();
        await F.Get<PaymentCommands>().Add(ticket.Id, new AddPaymentRequest(PaymentType.Mpesa, 1370, MpesaCode: "SKG7D8RT1Q"), None);
        var other = await Table7ForCarol("8");
        await F.Get<PaymentCommands>().Add(other.Id, new AddPaymentRequest(PaymentType.Mpesa, 500, MpesaCode: "SKG7FAKE00"), None);
        Transaction("SKG7D8RT1Q").Should().Match<MpesaTransaction>(t => t.Source == MpesaSource.Typed && !t.Confirmed);

        await Receive(FakeRelay.C2B("SKG7D8RT1Q", 1370));
        Transaction("SKG7D8RT1Q").Should().Match<MpesaTransaction>(t => t.Confirmed && t.Status == MpesaStatus.Applied && t.PayerName == "JOHN K");
        F.Read().MpesaTransactions.Count(t => t.TransactionId == "SKG7D8RT1Q").Should().Be(1);
        Payments(ticket.Id).Should().ContainSingle("it was already on the bill");

        await F.LoginOn("Administrator", "Counter B");
        var ledger = F.Get<MpesaLedger>();
        (await ledger.List(new MpesaLedgerFilters(), None)).Rows.Single(r => r.Code == "SKG7FAKE00").NeverConfirmed.Should().BeFalse();
        F.Clock.Advance(TimeSpan.FromHours(25));
        var later = await ledger.List(new MpesaLedgerFilters { From = new DateOnly(2026, 10, 20), To = new DateOnly(2026, 10, 21) }, None);
        later.Rows.Single(r => r.Code == "SKG7FAKE00").Should().Match<MpesaLedgerRow>(r => r.NeverConfirmed && r.Status == "NotConfirmed");
        later.Tiles.NeverConfirmed.Should().Be(new MpesaTile(1, 500));
        later.Rows.Single(r => r.Code == "SKG7D8RT1Q").NeverConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task A_code_is_used_once_across_received_typed_and_prompt_payments()
    {
        await SetUp();
        await Receive(FakeRelay.C2B("SKG7H2LM4P", 1370));
        var ticket = await Table7ForCarol();
        // Typing a code Safaricom already sent uses that payment.
        var typed = await F.Get<PaymentCommands>().Add(ticket.Id, new AddPaymentRequest(PaymentType.Mpesa, null, MpesaCode: "skg7h2lm4p"), None);
        typed.Payments!.Single().Amount.Should().Be(1370);
        Transaction("SKG7H2LM4P").Status.Should().Be(MpesaStatus.Applied);

        var other = await Table7ForCarol("8");
        var refused = await FluentActions.Awaiting(() => F.Get<PaymentCommands>().Add(other.Id, new AddPaymentRequest(PaymentType.Mpesa, null, MpesaCode: "SKG7H2LM4P"), None))
            .Should().ThrowAsync<ConflictException>();
        refused.Which.Code.Should().Be(SettleCodes.MpesaCodeUsed);
        refused.Which.Message.Should().Contain($"#{ticket.Number}");

        // A typed code stays used even after its payment is taken off (as in 1.0).
        var fake = await F.Get<PaymentCommands>().Add(other.Id, new AddPaymentRequest(PaymentType.Mpesa, 100, MpesaCode: "SKG7FAKE00"), None);
        await F.Get<PaymentCommands>().Remove(other.Id, fake.Payments!.Single().Id, None);
        await FluentActions.Awaiting(() => F.Get<PaymentCommands>().Add(other.Id, new AddPaymentRequest(PaymentType.Mpesa, 100, MpesaCode: "SKG7FAKE00"), None))
            .Should().ThrowAsync<ConflictException>();
    }

    // ---- The M-Pesa ledger ----

    [Fact]
    public async Task The_tiles_add_up_to_the_rows_and_not_from_pos_posts_a_balanced_entry()
    {
        await SetUp();
        var ticket = await Table7ForCarol();
        await Receive(FakeRelay.C2B("SKG7H2LM4P", 1370, billRef: ticket.Number.ToString()));
        await Receive(FakeRelay.C2B("SKG7G9QX2D", 1370));
        await Receive(FakeRelay.C2B("SKG7A4ZZ9C", 1000));
        await F.Get<SettleTicket>().Execute(ticket.Id, None);

        await F.LoginOn("Administrator", "Counter B");
        var ledger = F.Get<MpesaLedger>();
        var deposit = Transaction("SKG7A4ZZ9C").Id;
        await FluentActions.Awaiting(() => ledger.MarkNotFromPos(deposit, new NotFromPosRequest("", LedgerAccountCodes.OwnersCapital), None))
            .Should().ThrowAsync<FieldErrorsException>();
        var marked = await ledger.MarkNotFromPos(deposit, new NotFromPosRequest("Owner's deposit", LedgerAccountCodes.OwnersCapital), None);
        marked.Status.Should().Be("NotFromPos");

        var entry = F.Read().JournalEntries.Include(e => e.Lines).ThenInclude(l => l.Account).Single(e => e.SourceType == MpesaLedger.NotFromPosSource);
        entry.Lines.Sum(l => l.Debit).Should().Be(entry.Lines.Sum(l => l.Credit));
        entry.Lines.Single(l => l.Account!.Code == LedgerAccountCodes.MPesa).Debit.Should().Be(1000);
        entry.Lines.Single(l => l.Account!.Code == LedgerAccountCodes.OwnersCapital).Credit.Should().Be(1000);
        entry.BusinessDate.Should().Be(new DateOnly(2026, 10, 20));

        var list = await ledger.List(new MpesaLedgerFilters { Quick = Tausi.Application.Reports.QuickRange.Today }, None);
        list.Tiles.Received.Should().Be(new MpesaTile(3, 3740));
        (list.Tiles.Applied.Amount + list.Tiles.Unapplied.Amount + list.Tiles.NotFromPos.Amount).Should().Be(list.Tiles.Received.Amount);
        list.Tiles.Unapplied.Should().Be(new MpesaTile(1, 1370));
        list.Rows.Single(r => r.Code == "SKG7H2LM4P").Should().Match<MpesaLedgerRow>(r =>
            r.How == "QR on bill" && r.Status == "Applied" && r.Uses.Single().Cashier == "CAROL" && r.Uses.Single().Receipt != null);
        list.Rows.Single(r => r.Code == "SKG7G9QX2D").How.Should().Be("Paid to till");
        (await ledger.List(new MpesaLedgerFilters { OnlyProblems = true }, None)).Rows.Select(r => r.Code).Should().Equal("SKG7G9QX2D");

        var file = await ledger.Export(new MpesaLedgerFilters(), None);
        file.FileName.Should().EndWith(".xlsx");
        using var book = new ClosedXML.Excel.XLWorkbook(new MemoryStream(file.Content));
        var headers = book.Worksheets.First().RowsUsed().Select(r => r.Cells().Select(c => c.GetString()).ToList())
            .First(r => r.Contains("Code"));
        headers.Should().ContainInOrder("Time", "Code", "From", "Phone", "How", "Amount", "Applied", "Unapplied", "Status", "Tickets", "Receipts", "Cashier", "Note");
    }

    [Fact]
    public async Task Apply_to_ticket_adds_a_pending_payment_and_the_ledger_needs_its_permissions()
    {
        await SetUp();
        await Receive(FakeRelay.C2B("SKG7G9QX2D", 1370));
        var ticket = await Table7ForCarol();
        var id = Transaction("SKG7G9QX2D").Id;

        await F.LoginOn("edna", "Counter A");
        await FluentActions.Awaiting(() => F.Get<MpesaLedger>().List(new MpesaLedgerFilters(), None)).Should().ThrowAsync<PermissionDeniedException>();
        await F.LoginOn("Administrator", "Counter B");
        (await F.Get<MpesaLedger>().OpenTickets(None)).Should().ContainSingle(t => t.Id == ticket.Id && t.Remaining == 1370);
        var row = await F.Get<MpesaLedger>().ApplyToTicket(id, new ApplyToTicketRequest(ticket.Id, null), None);
        row.Status.Should().Be("Applied");
        row.Uses.Single().Settled.Should().BeFalse("the cashier completes the ticket as usual");
        Payments(ticket.Id).Single().Status.Should().Be(PaymentStatus.Pending);
        F.Read().AuditEntries.Should().Contain(a => a.Action == "mpesa.applied");
    }

    [Fact]
    public async Task Close_day_warns_about_unapplied_payments_but_still_closes()
    {
        await SetUp();
        await Receive(FakeRelay.C2B("SKG7G9QX2D", 1370));
        await Receive(FakeRelay.C2B("SKG7F1MN8T", 1500));
        await F.LoginOn("SAM", "Counter A");
        (await F.Get<GetDayStatus>().Execute(None)).Mpesa.Should().Be(new MpesaUnappliedDto(2, 2870));

        F.Relay.Down = true;
        var closed = await F.Get<CloseWorkPeriod>().Execute(None);
        closed.Mpesa.Should().Be(new MpesaUnappliedDto(2, 2870));
        closed.MpesaWarning.Should().Contain("couldn't be fetched");
        F.Read().WorkPeriods.Should().OnlyContain(w => !w.IsOpen);
    }

    // ---- Collecting from the relay ----

    [Fact]
    public async Task The_server_collects_only_while_tickets_are_open_and_nothing_is_lost_while_it_is_off()
    {
        await SetUp();
        var collector = F.Get<RelayCollector>();
        F.Relay.Waiting.Add(FakeRelay.C2B("SKG7H2LM4P", 1370));
        (await collector.Once(None)).Should().Be(RelayCollector.IdleCheck);
        F.Relay.Collections.Should().Be(0, "no open tickets: not connected");
        F.Get<RelayStatus>().State.Should().Be(RelayState.Idle);

        await Table7ForCarol();
        F.Relay.Down = true;
        await FluentActions.Awaiting(() => collector.Once(None)).Should().ThrowAsync<HttpRequestException>();
        F.Relay.Down = false;
        (await collector.Once(None)).Should().Be(TimeSpan.Zero);
        F.Get<RelayStatus>().State.Should().Be(RelayState.Connected);
        F.Read().MpesaTransactions.Should().ContainSingle(t => t.TransactionId == "SKG7H2LM4P");
        F.Relay.Acknowledged.Should().ContainSingle();

        // Catch-up (day open, Close day, Fetch now) works with no ticket open too, and acknowledges what it saved.
        F.Relay.Waiting.Add(FakeRelay.C2B("SKG7G9QX2D", 500));
        (await collector.Run(None)).Should().Be(1);
        F.Relay.Acknowledged.Should().HaveCount(2);
        await F.LoginOn("Administrator", "Counter B");
        (await F.Get<MpesaLedger>().FetchNow(None)).Should().Be(0);
    }

    [Fact]
    public async Task Ticket_numbers_in_references_are_matched_exactly()
    {
        MpesaApply.TicketNumberOf("1042").Should().Be(1042);
        MpesaApply.TicketNumberOf(" #1042 ").Should().Be(1042);
        MpesaApply.TicketNumberOf("1042A").Should().BeNull();
        MpesaApply.TicketNumberOf("Table 7").Should().BeNull();
        await Task.CompletedTask;
    }
}
