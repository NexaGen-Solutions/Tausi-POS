using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Common;
using Tausi.Application.Money;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Settling;
using Tausi.Application.Stock;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Service;
using Tausi.Domain.Stock;
using Tausi.Tests.Stock;

namespace Tausi.Tests.Money;

/// <summary>
/// The money side on the demo. The fake clock is 20 Oct 2026 18:00 UTC: 21:00 in Nairobi, so the
/// business date is 20 Oct.
/// </summary>
public class CashBookTests : StockTestBase
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    private MoneyActions Actions => F.Get<MoneyActions>();

    private async Task Nairobi() => await F.SetSetting(SettingsRegistry.BusinessTimeZoneKey, "Africa/Nairobi");

    private async Task<Guid> Category(string name = "Repairs") => (await F.Read().ExpenseCategories.SingleAsync(c => c.Name == name)).Id;

    private decimal Balance(string code, DateOnly? at = null) =>
        CashBook.Balances(F.Read(), at ?? Today, None).GetAwaiter().GetResult().GetValueOrDefault(code);

    private JournalEntry Entry(Guid? id) => F.Read().JournalEntries.Include(e => e.Lines).ThenInclude(l => l.Account).Single(e => e.Id == id);

    private async Task Admin()
    {
        await Nairobi();
        await AsAdmin();
    }

    // ---- Business date ----

    [Fact]
    public async Task A_work_period_that_runs_past_midnight_keeps_its_opening_date()
    {
        await Nairobi();
        F.Clock.Advance(TimeSpan.FromHours(4)); // 01:00 in Nairobi on the 21st
        await F.LoginOn("edna", "Counter A");
        var ticket = await Printed("7", ("Tusker Lager", 1));

        Entries(StockSelling.SaleSource).Single().BusinessDate.Should().Be(Today);
        F.Read().JournalEntries.Should().OnlyContain(e => e.BusinessDate != null);
        _ = ticket;
    }

    [Fact]
    public async Task Admin_dates_are_never_in_the_future_and_back_dated_at_most_the_setting()
    {
        await Admin();
        var future = () => Actions.AddExpense(new ExpenseRequest(Today.AddDays(1), 100, Category().Result, MoneyAccount.Safe, null, null, null, null), None);
        (await future.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be("date_future");
        var old = () => Actions.AddExpense(new ExpenseRequest(Today.AddDays(-32), 100, Category().Result, MoneyAccount.Safe, null, null, null, null), None);
        (await old.Should().ThrowAsync<DomainException>()).WithMessage("The date can be at most 31 days back.");

        (await Actions.AddExpense(new ExpenseRequest(Today.AddDays(-31), 100, await Category(), MoneyAccount.Safe, null, null, null, null), None))
            .BusinessDate.Should().Be(Today.AddDays(-31));
        await F.SetSetting(SettingsRegistry.BooksBackdateDays, 3);
        await FluentActions.Awaiting(() => Actions.AddExpense(new ExpenseRequest(Today.AddDays(-4), 100, Category().Result, MoneyAccount.Safe, null, null, null, null), None))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task An_entry_back_dated_into_a_closed_day_is_a_late_entry()
    {
        await Admin();
        F.Db.WorkPeriods.Add(new WorkPeriod
        {
            OpenedAt = F.Clock.GetUtcNow().AddDays(-2), BusinessDate = Today.AddDays(-2), IsOpen = false, ClosedAt = F.Clock.GetUtcNow().AddDays(-2).AddHours(8),
            OpenedByUserId = F.User("SAM").Id,
        });
        await F.Db.SaveChangesAsync();

        var late = await Actions.AddExpense(new ExpenseRequest(Today.AddDays(-2), 500, await Category(), MoneyAccount.Safe, "Ice", null, null, null), None);
        var onTime = await Actions.AddExpense(new ExpenseRequest(Today, 500, await Category(), MoneyAccount.Safe, "Ice", null, null, null), None);

        Entry(late.JournalEntryId).IsLate.Should().BeTrue();
        Entry(onTime.JournalEntryId).IsLate.Should().BeFalse();
        (await F.Get<CashBook>().Day(Today.AddDays(-2), false, None)).Rows.Should().ContainSingle(r => r.Late);
    }

    [Fact]
    public async Task The_back_fill_dates_old_entries_from_their_work_period_or_their_own_date_and_gives_sales_their_shift()
    {
        await Nairobi();
        var period = F.Read().WorkPeriods.Single();
        var sale = await Printed("7", ("Tusker Lager", 1));
        await F.Db.JournalEntries.ExecuteUpdateAsync(u => u.SetProperty(e => e.BusinessDate, (DateOnly?)null));
        await F.Db.WorkPeriods.ExecuteUpdateAsync(u => u.SetProperty(p => p.BusinessDate, (DateOnly?)null));
        F.Db.JournalEntries.Add(new JournalEntry { Date = F.Clock.GetUtcNow().AddDays(-5), Memo = "Old", SourceType = "Test", CreatedAt = F.Clock.GetUtcNow() });
        await F.Db.SaveChangesAsync();

        (await F.Get<LedgerBackfill>().Run(None)).Should().BeGreaterThan(0);

        F.Read().WorkPeriods.Single().BusinessDate.Should().Be(Today);
        F.Read().JournalEntries.Single(e => e.Memo == "Old").BusinessDate.Should().Be(Today.AddDays(-5));
        F.Read().JournalEntries.Where(e => e.WorkPeriodId == period.Id).Should().OnlyContain(e => e.BusinessDate == Today);
        (await F.Get<LedgerBackfill>().Run(None)).Should().Be(0, "running it again changes nothing");
        _ = sale;
    }

    // ---- Money actions ----

    [Theory]
    [InlineData(MoneyAccount.Safe, LedgerAccountCodes.Safe)]
    [InlineData(MoneyAccount.Bank, LedgerAccountCodes.Bank)]
    [InlineData(MoneyAccount.Mpesa, LedgerAccountCodes.MPesa)]
    public async Task An_expense_debits_expenses_with_its_category_and_credits_where_it_was_paid_from(MoneyAccount from, string code)
    {
        await Admin();
        var repairs = await Category();

        var expense = await Actions.AddExpense(new ExpenseRequest(null, 1500, repairs, from, "Fundi Joe", "INV-9", "Tap fixed", null), None);

        expense.Status.Should().Be("Posted");
        expense.Title.Should().Be("Tap fixed (expense)", "what was bought reads first");
        var entry = Entry(expense.JournalEntryId);
        entry.Lines.Single(l => l.Account!.Code == LedgerAccountCodes.Expenses).Should().Match<JournalLine>(l => l.Debit == 1500 && l.ExpenseCategoryId == repairs);
        entry.Lines.Single(l => l.Account!.Code == code).Credit.Should().Be(1500);
        entry.BusinessDate.Should().Be(Today);
    }

    [Fact]
    public async Task Paying_a_supplier_never_pays_more_than_is_owed()
    {
        await Admin();
        await Deliver("4471"); // 4 crates at 2,500 on credit: KBL is owed 10,000

        var refused = () => Actions.PaySupplier(new SupplierPaymentRequest(Supplier(), 10_001, MoneyAccount.Bank, null, null, null), None);
        (await refused.Should().ThrowAsync<DomainException>()).WithMessage("KBL is owed 10,000. Pay 10,000 or less.");

        var paid = await Actions.PaySupplier(new SupplierPaymentRequest(Supplier(), 6000, MoneyAccount.Bank, "EFT 1", null, null), None);

        paid.Title.Should().Be("Paid KBL");
        var entry = Entry(paid.JournalEntryId);
        Dr(entry, LedgerAccountCodes.SupplierPayables).Should().Be(6000);
        Cr(entry, LedgerAccountCodes.Bank).Should().Be(6000);
        (await SupplierBalances.Owed(F.Read(), None))[Supplier()].Should().Be(4000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public async Task Moving_money_moves_it_and_a_fee_goes_to_bank_charges(decimal fee)
    {
        await Admin();
        var moved = await Actions.MoveMoney(new MoveMoneyRequest(MoneyAccount.Safe, MoneyAccount.Bank, 15_000, fee, "Deposit slip 3", null), None);

        moved.Title.Should().Be("Banked cash");
        var entry = Entry(moved.JournalEntryId);
        Dr(entry, LedgerAccountCodes.Bank).Should().Be(15_000);
        Cr(entry, LedgerAccountCodes.Safe).Should().Be(15_000 + fee);
        Dr(entry, LedgerAccountCodes.BankCharges).Should().Be(fee);
        moved.Warning.Should().NotBeNull("the safe had nothing in it in the books");
        await FluentActions.Awaiting(() => Actions.MoveMoney(new MoveMoneyRequest(MoneyAccount.Bank, MoneyAccount.Bank, 1, null, null, null), None))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Owner_money_and_other_income_post_to_their_accounts()
    {
        await Admin();
        var took = await Actions.OwnerMoney(new OwnerMoneyRequest(true, MoneyAccount.Safe, 2000, "School fees", null), None);
        var put = await Actions.OwnerMoney(new OwnerMoneyRequest(false, MoneyAccount.Bank, 50_000, "Capital", null), None);
        var rent = await Actions.OtherIncome(new OtherIncomeRequest(MoneyAccount.Mpesa, 3000, "Rent from the kiosk", "Mama Njeri", null), None);

        Dr(Entry(took.JournalEntryId), LedgerAccountCodes.OwnersDrawings).Should().Be(2000);
        Cr(Entry(took.JournalEntryId), LedgerAccountCodes.Safe).Should().Be(2000);
        Dr(Entry(put.JournalEntryId), LedgerAccountCodes.Bank).Should().Be(50_000);
        Cr(Entry(put.JournalEntryId), LedgerAccountCodes.OwnersCapital).Should().Be(50_000);
        Dr(Entry(rent.JournalEntryId), LedgerAccountCodes.MPesa).Should().Be(3000);
        Cr(Entry(rent.JournalEntryId), LedgerAccountCodes.OtherIncome).Should().Be(3000);
        await FluentActions.Awaiting(() => Actions.OtherIncome(new OtherIncomeRequest(MoneyAccount.Mpesa, 1, " ", null, null), None))
            .Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Opening_balances_are_entered_once_and_lock()
    {
        await Admin();
        var opening = await Actions.OpeningBalances(new OpeningBalancesRequest(20_000, 150_000, 5000, [new OpeningSupplier(Supplier(), 30_000)], null), None);

        var entry = Entry(opening.JournalEntryId);
        Dr(entry, LedgerAccountCodes.Safe).Should().Be(20_000);
        Dr(entry, LedgerAccountCodes.Bank).Should().Be(150_000);
        Dr(entry, LedgerAccountCodes.MPesa).Should().Be(5000);
        Cr(entry, LedgerAccountCodes.SupplierPayables).Should().Be(30_000);
        Cr(entry, LedgerAccountCodes.OpeningBalances).Should().Be(175_000);
        Dr(entry, LedgerAccountCodes.OpeningBalances).Should().Be(30_000);
        (await SupplierBalances.Owed(F.Read(), None))[Supplier()].Should().Be(30_000);
        (await Actions.OpeningLocked(None)).Should().BeTrue();
        var again = () => Actions.OpeningBalances(new OpeningBalancesRequest(1, null, null, null, null), None);
        (await again.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(MoneyCodes.OpeningLocked);
        var tiles = await CashBook.Tiles(F.Read(), Today, None);
        tiles.CashOnHand.Should().Be(20_000);
        tiles.Bank.Should().Be(150_000);
        tiles.OwedToSuppliers.Should().Be(30_000);

        await F.LoginOn("SAM", "Counter A");
        await FluentActions.Awaiting(() => Actions.OpeningBalances(new OpeningBalancesRequest(1, null, null, null, null), None))
            .Should().ThrowAsync<PermissionDeniedException>("only settings.manage");
    }

    [Fact]
    public async Task Reverse_posts_the_exact_opposite_linked_both_ways_once()
    {
        await Admin();
        var expense = await Actions.AddExpense(new ExpenseRequest(null, 1500, await Category(), MoneyAccount.Safe, null, null, null, null), None);
        var safe = Balance(LedgerAccountCodes.Safe);

        var reversed = await Actions.Reverse(expense.Id, new ReverseMoneyRequest("Entered twice"), None);

        reversed.ReversalStatus.Should().Be("Posted");
        var original = Entry(expense.JournalEntryId);
        var opposite = Entry(reversed.ReversalEntryId);
        original.ReversedByEntryId.Should().Be(opposite.Id);
        opposite.ReversesEntryId.Should().Be(original.Id);
        opposite.Lines.Select(l => (l.Account!.Code, l.Debit, l.Credit, l.ExpenseCategoryId))
            .Should().BeEquivalentTo(original.Lines.Select(l => (l.Account!.Code, l.Credit, l.Debit, l.ExpenseCategoryId)));
        Balance(LedgerAccountCodes.Safe).Should().Be(safe + 1500);
        var twice = () => Actions.Reverse(expense.Id, new ReverseMoneyRequest("Again"), None);
        (await twice.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(MoneyCodes.AlreadyReversed);
        var rows = (await F.Get<CashBook>().Day(Today, false, None)).Rows;
        rows.Should().Contain(r => r.Reversed).And.Contain(r => r.Reversal && r.Text.StartsWith("Reversed:"));
    }

    // ---- Shared admin approvals ----

    [Fact]
    public async Task Money_waits_pending_until_someone_else_allowed_approves_and_stock_uses_the_same_queue()
    {
        await Nairobi();
        await F.SetPermission("Supervisor", PermissionKeys.StockReceive, PermissionValue.NeedsApproval);
        await AsAdmin();
        await Deliver("1");
        var sam = await F.LoginOn("SAM", "Counter A");

        var payment = await Actions.PaySupplier(new SupplierPaymentRequest(Supplier(), 4000, MoneyAccount.Bank, null, null, null), None);
        var delivery = await Deliver("2");

        payment.Status.Should().Be("Pending");
        payment.JournalEntryId.Should().BeNull();
        delivery.Status.Should().Be("Pending");
        var queue = await F.Get<AwaitingApproval>().Execute(None);
        queue.Should().Contain(q => q.Id == payment.Id && q.Kind == "Money").And.Contain(q => q.Id == delivery.Id && q.Kind == "Delivery");
        await FluentActions.Awaiting(() => Actions.Approve(payment.Id, None)).Should().ThrowAsync<PermissionDeniedException>();

        await AsAdmin();
        (await Actions.Approve(payment.Id, None)).Status.Should().Be("Posted");
        (await F.Get<DeliveryCommands>().Approve(delivery.Id, None)).Status.Should().Be("Posted");
        (await F.Get<AwaitingApproval>().Execute(None)).Should().BeEmpty();

        await F.SetPermission("Admin", PermissionKeys.CashBook, PermissionValue.NeedsApproval);
        await F.SetPermission("Supervisor", PermissionKeys.CashBook, PermissionValue.Allowed);
        var own = await Actions.MoveMoney(new MoveMoneyRequest(MoneyAccount.Bank, MoneyAccount.Safe, 100, null, null, null), None);
        await F.SetPermission("Admin", PermissionKeys.CashBook, PermissionValue.Allowed);
        var self = () => Actions.Approve(own.Id, None);
        (await self.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be(StockCodes.OwnDocument);
        F.ActAs(sam);
        (await Actions.Reject(own.Id, new RejectMoneyRequest("Not needed"), None)).Status.Should().Be("Rejected");
        Entries(MoneyActions.LedgerSource).Should().HaveCount(1, "only the approved payment posted");
    }

    // ---- A full day: trial balance, summary, tiles and entry text ----

    /// <summary>
    /// A day with sales (cash and M-Pesa), a void, a gift, a discount, a cash-up 100 short, a
    /// delivery, waste, a count, an expense, a pay-out and banking.
    /// </summary>
    private async Task FullDay()
    {
        await Nairobi();
        var t1 = await Printed("7", ("Tusker Lager", 3), ("Guiness", 2), ("Pool Table", 1));
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(t1.Id);
        await Void(t1.Id, LineOf(t1, "Guiness"), 1, poured: true);
        await Gift(t1.Id, LineOf(t1, "Tusker Lager"));
        await Discount(t1.Id, DiscountKind.Percent, 10);
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(t1.Id);
        await StartShift(1000);
        Later();
        var due = (await Detail(t1.Id)).AmountDue;
        await F.Get<PaymentCommands>().Add(t1.Id, new AddPaymentRequest(PaymentType.Cash, null, 300, null, null, null), None);
        await F.Get<PaymentCommands>().Add(t1.Id, new AddPaymentRequest(PaymentType.Mpesa, due - 300, null, "SJK3ABC777", null, null), None);
        await F.Get<SettleTicket>().Execute(t1.Id, None);
        var shift = (await F.Get<Application.Cash.ShiftQueries>().Current(None)).Shift!;
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.CashExpense);
        await F.Get<Application.Cash.DrawerMovements>().PayOut(shift.Id, new Application.Cash.DrawerMovementRequest(200, await Category("Transport"), "Taxi"), None);
        await F.Get<Application.Cash.CountShift>().Execute(shift.Id, new Application.Cash.CountShiftRequest(new Application.Cash.CashCount(Total: 1000 + 300 - 200 - 100)), None);
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.DayOpenClose);
        await F.Get<Application.Cash.CloseShift>().Execute(shift.Id, None);

        await AsAdmin();
        await Deliver("D1", DeliveryPayment.Cash);
        await F.Get<WasteAndTransferCommands>().RecordWaste(new RecordWasteRequest(Location("Bar"), Item("Guiness"), 1, null, "Broken", null), None);
        await Actions.AddExpense(new ExpenseRequest(null, 1500, await Category(), MoneyAccount.Safe, "Duka", null, "Ice and charcoal", null), None);
        await Actions.MoveMoney(new MoveMoneyRequest(MoneyAccount.Safe, MoneyAccount.Bank, 500, 50, null, null), None);
    }

    [Fact]
    public async Task Over_a_full_day_debits_equal_credits_and_the_summary_equals_the_ledger()
    {
        await FullDay();

        var trial = await Accounts.Trial(F.Read(), Today, None);
        trial.Difference.Should().Be(0);
        trial.Debits.Should().BeGreaterThan(0);
        AllEntriesBalance();

        var summary = await Summaries.For(F.Read(), Today, includeLate: true, None);
        var db = F.Read();
        var lines = db.JournalLines.Include(l => l.Account).Where(l => db.JournalEntries.Any(e => e.Id == l.JournalEntryId && e.BusinessDate == Today)).ToList();
        decimal Net(string code) => lines.Where(l => l.Account!.Code == code).Sum(l => l.Debit - l.Credit);
        summary.NetSales.Should().Be(-Net(LedgerAccountCodes.Sales) - Net(LedgerAccountCodes.Rounding));
        summary.Vat.Should().Be(-Net(LedgerAccountCodes.VatPayable));
        summary.CostOfSales.Should().Be(Net(LedgerAccountCodes.CostOfSales)).And.BeGreaterThan(0);
        summary.Expenses.Should().Be(Net(LedgerAccountCodes.Expenses)).And.Be(1700);
        summary.ExpensesByCategory.Should().BeEquivalentTo([new NamedAmount("Repairs", 1500), new NamedAmount("Transport", 200)]);
        summary.StockLossAndCash.Should().Be(Net(LedgerAccountCodes.StockLoss) + Net(LedgerAccountCodes.CashOverShort));
        summary.StockLossParts.Select(p => p.Name).Should().Contain(["Waste", "Poured voids", "Cash over/short"]);
        summary.StockLossParts.Single(p => p.Name == "Cash over/short").Amount.Should().Be(100);
        summary.BankCharges.Should().Be(50);
        summary.Net.Should().Be(summary.GrossProfit - summary.Expenses - summary.StockLossAndCash - summary.BankCharges + summary.OtherIncome);
        summary.NoRecipeItems.Should().Equal("Pool Table");
    }

    [Fact]
    public async Task The_cash_book_groups_sales_per_shift_and_type_and_says_each_entry_in_plain_words()
    {
        await FullDay();

        var book = await F.Get<CashBook>().Day(Today, false, None);
        var texts = book.Rows.Select(r => r.Text).ToList();
        texts.Should().Contain(["Cash sales · CAROL shift", "M-Pesa sales · CAROL shift", "Float · CAROL shift", "Transport (pay out) · CAROL shift",
            "Cash short · CAROL shift", "Drawer to the safe · CAROL shift", "Ice and charcoal (expense)", "Banked cash"]);
        book.Rows.Count(r => r.Text == "Cash sales · CAROL shift").Should().Be(1);
        book.Rows.Single(r => r.Text == "Banked cash").Account.Should().Be("Safe → Bank");
        book.Rows.Should().Contain(r => r.Text.StartsWith("Bank charges"));
        texts.Should().NotContain("Cost of sales", "Money only by default");

        var all = await F.Get<CashBook>().Day(Today, true, None);
        all.Rows.Select(r => r.Text).Should().Contain(["Cost of sales", "Voided pours", "Waste · Broken"]);

        var tiles = book.Tiles;
        tiles.InOpenDrawers.Should().Be(0, "every shift is closed");
        tiles.CashOnHand.Should().Be(Balance(LedgerAccountCodes.Safe));
        tiles.MpesaReceived.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_tiles_say_what_is_still_in_an_open_drawer()
    {
        await Nairobi();
        await F.LoginOn("CAROL", "Counter A");
        await StartShift(1500);

        var tiles = await CashBook.Tiles(F.Read(), Today, None);

        tiles.InOpenDrawers.Should().Be(1500);
        tiles.CashOnHand.Should().Be(0, "the float came from the safe");
    }

    // ---- Attachments, PDF and CSV ----

    [Fact]
    public async Task A_receipt_photo_is_checked_kept_private_and_only_opened_with_cash_book()
    {
        await Admin();
        var attachments = F.Get<MoneyAttachments>();
        var gif = () => attachments.Upload(new FileUpload("r.gif", "image/gif", 6, new MemoryStream("GIF89a"u8.ToArray())), None);
        (await gif.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be("file_type");
        var big = () => attachments.Upload(new FileUpload("r.pdf", "application/pdf", MoneyAttachments.MaxBytes + 1, new MemoryStream(new byte[10])), None);
        (await big.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be("file_size");

        var file = await attachments.Upload(new FileUpload("r.pdf", "application/pdf", 9, new MemoryStream("%PDF-1.4\n"u8.ToArray())), None);
        F.Read().StoredFiles.Single(f => f.Id == file.FileId).IsPrivate.Should().BeTrue();
        var expense = await Actions.AddExpense(new ExpenseRequest(null, 1500, await Category(), MoneyAccount.Safe, null, null, null, file.FileId), None);
        expense.HasAttachment.Should().BeTrue();

        await F.LoginOn("edna", "Counter A");
        await FluentActions.Awaiting(() => F.Get<MoneyAttachments>().Open(expense.Id, None)).Should().ThrowAsync<PermissionDeniedException>();
    }

    [Fact]
    public async Task The_summary_pdf_and_the_csv_export()
    {
        await FullDay();

        var pdf = Encoding.Latin1.GetString(await F.Get<CashBookPrint>().Pdf(Today, None));
        pdf.Should().StartWith("%PDF").And.Contain("Daily summary").And.Contain("Gross profit").And.Contain("Net for the day");

        var csv = await F.Get<CashBook>().Csv(Today, false, None);
        var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        rows[0].Should().Be("Date,Time,Entry,Account,In,Out,Late,Reversed");
        rows.Should().Contain(r => r.StartsWith("2026-10-20,") && r.Contains("Banked cash") && r.Contains("Safe → Bank") && r.Contains(",500.00,"));
    }

    [Fact]
    public async Task The_accounts_view_groups_balances_and_lists_an_accounts_lines_with_a_running_balance()
    {
        await Admin();
        await Actions.OpeningBalances(new OpeningBalancesRequest(20_000, 150_000, 5000, null, null), None);
        await Actions.AddExpense(new ExpenseRequest(null, 1500, await Category(), MoneyAccount.Safe, null, null, null, null), None);

        var accounts = await F.Get<Accounts>().Balances(Today, None);
        accounts.Trial.Difference.Should().Be(0);
        var money = accounts.Groups.Single(g => g.Name == "Money");
        money.Accounts.Single(a => a.Code == LedgerAccountCodes.Safe).Balance.Should().Be(18_500);
        accounts.Groups.Select(g => g.Name).Should().ContainInOrder("Money", "Stock", "Owed", "Sales and income", "Costs and expenses", "Owner");

        var ledger = await F.Get<Accounts>().Ledger(LedgerAccountCodes.Safe, Today, Today, None);
        ledger.Lines.Select(l => l.Balance).Should().Equal(20_000, 18_500);
        ledger.Closing.Should().Be(18_500);
    }
}
