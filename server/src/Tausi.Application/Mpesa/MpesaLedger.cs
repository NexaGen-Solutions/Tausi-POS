using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Ledger;
using Tausi.Application.Money;
using Tausi.Application.Reports;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Domain.Mpesa;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Mpesa;

/// <summary>How a payment reached the POS, in words (board 27's "How").</summary>
public static class MpesaHow
{
    public static string Of(MpesaTransaction t, bool paybill) => t.Source switch
    {
        MpesaSource.Stk => "STK prompt",
        MpesaSource.Typed => "Code typed",
        _ when t.MatchedTicketId is not null => "QR on bill",
        _ => paybill ? "Paid to paybill" : "Paid to till",
    };

    /// <summary>A code a cashier typed that Safaricom hasn't confirmed after 24 hours: maybe a fake SMS.</summary>
    public static bool NeverConfirmed(MpesaTransaction t, DateTimeOffset now) =>
        t.Source == MpesaSource.Typed && !t.Confirmed && t.At <= now - NeverConfirmedAfter;

    public static readonly TimeSpan NeverConfirmedAfter = TimeSpan.FromHours(24);
}

/// <summary>A payment on the settle screen's Payments received list (board 24).</summary>
public sealed record ReceivedPaymentDto(
    Guid Id, string Code, DateTimeOffset At, string? PayerName, string? Phone, decimal Amount, decimal Available, string How, string Match);

/// <summary>
/// The cashier's live list (board 24): M-Pesa payments from Safaricom in the last 24 hours that
/// aren't fully on a bill yet. Matches for this bill: "QrOnThisBill" (its reference is this ticket),
/// "SameAmount" (what's left equals what this bill owes and its bill printed in the last 30 minutes,
/// a suggestion only), else "None". Needs <c>ticket.settle</c>.
/// </summary>
public sealed class MpesaReceived(ITausiDb db, ICurrentSession session, IPermissionService permissions, ISecretProtector secrets, TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    public static readonly TimeSpan BillRecent = TimeSpan.FromMinutes(30);

    public async Task<IReadOnlyList<ReceivedPaymentDto>> For(Guid ticketId, CancellationToken ct)
    {
        var user = session.RequireUser();
        if (await permissions.Check(user, PermissionKeys.TicketSettle, ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(PermissionKeys.TicketSettle);
        var ticket = await db.Tickets.AsNoTracking().Include(k => k.Lines).Include(k => k.Payments).FirstOrDefaultAsync(k => k.Id == ticketId, ct)
            ?? throw new NotFoundException("Ticket not found.");
        ticket.ApplyRounding(await MoneySettings.Rounding(db, ct));
        var now = clock.GetUtcNow();
        var since = now - Window;
        var paybill = (await MpesaSettings.Load(db, secrets, ct)).Paybill;
        var open = await db.MpesaTransactions.AsNoTracking()
            .Where(t => t.Confirmed && (t.Status == MpesaStatus.Unapplied || t.Status == MpesaStatus.PartlyApplied) && t.ReceivedAt >= since)
            .ToListAsync(ct);
        var recentBill = ticket.BillPrintedAt is { } printed && now - printed <= BillRecent;
        return open
            .Select(t => new ReceivedPaymentDto(t.Id, t.CodeKnown ? t.TransactionId : "", t.At, t.PayerName, t.Phone, t.Amount, t.Available,
                MpesaHow.Of(t, paybill),
                t.MatchedTicketId == ticket.Id ? "QrOnThisBill" : recentBill && t.Available == ticket.Remaining ? "SameAmount" : "None"))
            .OrderBy(r => r.Match == "QrOnThisBill" ? 0 : r.Match == "SameAmount" ? 1 : 2).ThenByDescending(r => r.At)
            .ToList();
    }

    /// <summary>How many are waiting (the "Payments received (n)" tab).</summary>
    public async Task<int> Count(CancellationToken ct)
    {
        session.RequireUser();
        var since = clock.GetUtcNow() - Window;
        return await db.MpesaTransactions.CountAsync(
            t => t.Confirmed && (t.Status == MpesaStatus.Unapplied || t.Status == MpesaStatus.PartlyApplied) && t.ReceivedAt >= since, ct);
    }
}

public sealed record MpesaLedgerFilters
{
    public QuickRange? Quick { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public MpesaStatus? Status { get; init; }
    public MpesaSource? Source { get; init; }
    public bool OnlyProblems { get; init; }
}

public sealed record MpesaTile(int Count, decimal Amount);

public sealed record MpesaTiles(MpesaTile Received, MpesaTile Applied, MpesaTile Unapplied, MpesaTile NotFromPos, MpesaTile NeverConfirmed);

/// <summary>One row of board 27: its status in words, with the ticket, receipt and cashier when applied.</summary>
public sealed record MpesaLedgerRow(
    Guid Id, DateTimeOffset At, string Code, string? PayerName, string? Phone, string How, decimal Amount, decimal Available, string Status,
    bool Confirmed, bool NeverConfirmed, IReadOnlyList<MpesaUse> Uses, string? Note, Guid? TicketId);

public sealed record MpesaUse(Guid TicketId, long TicketNumber, long? Receipt, string? Cashier, decimal Amount, bool Settled);

public sealed record MpesaLedgerDto(string Period, MpesaTiles Tiles, IReadOnlyList<MpesaLedgerRow> Rows);

public sealed record ApplyToTicketRequest(Guid TicketId, decimal? Amount);

public sealed record NotFromPosRequest(string Reason, string Account);

public sealed record OpenTicketOption(Guid Id, long Number, string Where, decimal Remaining);

/// <summary>
/// The M-Pesa ledger (board 27) on the admin site, and read-only on the terminal: every M-Pesa
/// payment in the dates, with tiles that add up to the rows. Needs <c>reports.all</c> or
/// <c>cash.book</c>. Apply to ticket needs <c>ticket.settle</c>; Mark as not from POS needs
/// <c>cash.book</c> and posts Dr M-Pesa, Cr Other income or Owner's capital. All actions are audited.
/// Unapplied payments aren't in the books until they are applied (and settled) or marked.
/// </summary>
public sealed class MpesaLedger(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, ISecretProtector secrets, MpesaApply apply,
    ILedgerPostingService ledger, IAuditLog audit, IPosEvents events, IMpesaCatchUp catchUp, TimeProvider clock)
{
    public const string NotFromPosSource = "MpesaNotFromPos";
    public static readonly string[] NotFromPosAccounts = [LedgerAccountCodes.OtherIncome, LedgerAccountCodes.OwnersCapital];

    public async Task<MpesaLedgerDto> List(MpesaLedgerFilters f, CancellationToken ct)
    {
        await RequireView(ct);
        var zone = await BusinessDates.Zone(db, ct);
        var now = clock.GetUtcNow();
        var today = BusinessDates.Of(now, zone);
        var (from, to) = f.Quick is { } q ? ReportContext.Range(q, today) : (f.From ?? today, f.To ?? f.From ?? today);
        if (to < from) throw new FieldErrorsException(new Dictionary<string, string> { ["to"] = "The end date is before the start date." });
        var (start, end) = (BusinessDates.Midnight(from, zone), BusinessDates.Midnight(to.AddDays(1), zone));
        var paybill = (await MpesaSettings.Load(db, secrets, ct)).Paybill;

        var all = await db.MpesaTransactions.AsNoTracking().Where(t => t.At >= start && t.At < end).OrderByDescending(t => t.At).ToListAsync(ct);
        var ids = all.Select(t => t.Id).ToList();
        var uses = await (from p in db.Payments.AsNoTracking()
                          join k in db.Tickets.AsNoTracking() on p.TicketId equals k.Id
                          where p.MpesaTransactionId != null && ids.Contains(p.MpesaTransactionId.Value) && p.Status != PaymentStatus.Removed
                          select new { Transaction = p.MpesaTransactionId!.Value, k.Id, k.Number, k.ReceiptNumber, k.SettledByUserId, p.Amount, Settled = k.Status == TicketStatus.Settled })
            .ToListAsync(ct);
        var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        MpesaLedgerRow Row(MpesaTransaction t)
        {
            var used = uses.Where(u => u.Transaction == t.Id)
                .Select(u => new MpesaUse(u.Id, u.Number, u.ReceiptNumber, u.SettledByUserId is { } c ? names.GetValueOrDefault(c) : null, u.Amount, u.Settled))
                .ToList();
            var never = MpesaHow.NeverConfirmed(t, now);
            var status = t.Status == MpesaStatus.NotFromPos ? "NotFromPos"
                : t.Source == MpesaSource.Typed && !t.Confirmed ? "NotConfirmed"
                : t.Status.ToString();
            var note = t.Status == MpesaStatus.NotFromPos
                ? $"{t.NotFromPosReason} · {(t.NotFromPosByUserId is { } by ? names.GetValueOrDefault(by) : "")}"
                : t.Source == MpesaSource.Typed && !t.Confirmed
                    ? $"typed by {(t.TypedByUserId is { } typer ? names.GetValueOrDefault(typer) : "?")} · no matching payment from Safaricom{(never ? "" : " yet")}"
                    : null;
            return new MpesaLedgerRow(t.Id, t.At, t.CodeKnown ? t.TransactionId : "", t.PayerName, t.Phone, MpesaHow.Of(t, paybill), t.Amount,
                t.Available, status, t.Confirmed, never, used, note, used.FirstOrDefault()?.TicketId ?? t.MatchedTicketId);
        }

        var rows = all.Select(Row).ToList();
        var fromSafaricom = all.Where(t => t.Confirmed).ToList();
        var tiles = new MpesaTiles(
            new MpesaTile(fromSafaricom.Count, fromSafaricom.Sum(t => t.Amount)),
            new MpesaTile(uses.Where(u => fromSafaricom.Any(t => t.Id == u.Transaction)).Select(u => u.Id).Distinct().Count(),
                fromSafaricom.Where(t => t.Status != MpesaStatus.NotFromPos).Sum(t => t.AmountApplied)),
            new MpesaTile(fromSafaricom.Count(t => t.Available > 0), fromSafaricom.Sum(t => t.Available)),
            new MpesaTile(all.Count(t => t.Status == MpesaStatus.NotFromPos), all.Where(t => t.Status == MpesaStatus.NotFromPos).Sum(t => t.Amount - t.AmountApplied)),
            new MpesaTile(rows.Count(r => r.NeverConfirmed), rows.Where(r => r.NeverConfirmed).Sum(r => r.Amount)));

        IEnumerable<MpesaLedgerRow> shown = rows;
        if (f.Status is { } status) shown = shown.Where(r => r.Status == status.ToString());
        if (f.Source is { } source) shown = shown.Where(r => all.First(t => t.Id == r.Id).Source == source);
        if (f.OnlyProblems) shown = shown.Where(r => r.Available > 0 && r.Confirmed && r.Status != "NotFromPos" || r.NeverConfirmed);
        var label = from == to ? from.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)
            : $"{from.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} – {to.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
        return new MpesaLedgerDto(label, tiles, shown.ToList());
    }

    /// <summary>Open bills that still owe something, to apply a payment to.</summary>
    public async Task<IReadOnlyList<OpenTicketOption>> OpenTickets(CancellationToken ct)
    {
        await RequireView(ct);
        var rounding = await MoneySettings.Rounding(db, ct);
        var open = await db.Tickets.AsNoTracking().Include(k => k.Lines).Include(k => k.Payments).Include(k => k.Table)
            .Where(k => k.Status == TicketStatus.Open).OrderBy(k => k.Number).ToListAsync(ct);
        foreach (var k in open) k.ApplyRounding(rounding);
        return open.Where(k => k.Remaining > 0).Select(k => new OpenTicketOption(k.Id, k.Number, TicketPrints.Where(k), k.Remaining)).ToList();
    }

    public async Task<MpesaLedgerRow> ApplyToTicket(Guid id, ApplyToTicketRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        if (await permissions.Check(user, PermissionKeys.TicketSettle, ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(PermissionKeys.TicketSettle);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var transaction = await db.MpesaTransactions.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Payment not found.");
        if (!transaction.Confirmed) throw new DomainException("A typed code is already on its bill.");
        if (transaction.Available <= 0) throw new ConflictException($"{transaction.TransactionId} has been used in full.", MpesaCodes.UsedUp);
        var applied = await apply.ToTicket(transaction, r.TicketId, r.Amount, user.UserId, null, ct)
            ?? throw new DomainException("That bill is closed, owes nothing, or the day isn't open.");
        audit.Write(new AuditRecord(AuditActions.MpesaApplied, nameof(MpesaTransaction), transaction.Id.ToString(), null,
            new { transaction.TransactionId, ticket = applied.TicketNumber, applied.Amount, from = "M-Pesa ledger" }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await apply.Changed(applied.TicketId, ct);
        await events.Publish(PosEventNames.MpesaReceived, new { id }, ct);
        var date = BusinessDates.Of(transaction.At, await BusinessDates.Zone(db, ct));
        return (await List(new MpesaLedgerFilters { From = date, To = date }, ct)).Rows.Single(x => x.Id == id);
    }

    public async Task<MpesaLedgerRow> MarkNotFromPos(Guid id, NotFromPosRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        if (await permissions.Check(user, PermissionKeys.CashBook, ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(PermissionKeys.CashBook);
        var reason = r.Reason?.Trim() ?? "";
        if (reason.Length == 0 || reason.Length > MpesaTransaction.MaxReasonLength)
            throw new FieldErrorsException(new Dictionary<string, string> { ["reason"] = "Say what this money was for." });
        if (!NotFromPosAccounts.Contains(r.Account))
            throw new FieldErrorsException(new Dictionary<string, string> { ["account"] = "Choose Other income or Owner's capital." });
        var transaction = await db.MpesaTransactions.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Payment not found.");
        if (!transaction.Confirmed) throw new DomainException("Only payments from Safaricom can be marked as not from the POS.");
        var amount = transaction.Available;
        if (amount <= 0) throw new ConflictException("This payment is already on bills.", MpesaCodes.UsedUp);

        var zone = await BusinessDates.Zone(db, ct);
        await ledger.Post(new JournalEntryDraft(transaction.At, $"M-Pesa {transaction.TransactionId} not from the POS: {reason}", NotFromPosSource,
            transaction.Id.ToString(),
            [
                JournalLineDraft.Dr(LedgerAccountCodes.MPesa, amount, transaction.TransactionId),
                JournalLineDraft.Cr(r.Account, amount, reason),
            ], BusinessDate: BusinessDates.Of(transaction.At, zone)), ct);
        transaction.Status = MpesaStatus.NotFromPos;
        transaction.NotFromPosReason = reason;
        transaction.NotFromPosAccount = r.Account;
        transaction.NotFromPosByUserId = user.UserId;
        transaction.NotFromPosAt = clock.GetUtcNow();
        audit.Write(new AuditRecord(AuditActions.MpesaNotFromPos, nameof(MpesaTransaction), transaction.Id.ToString(),
            new { transaction.TransactionId, status = "Unapplied" }, new { transaction.TransactionId, amount, reason, account = r.Account }));
        await db.SaveChangesAsync(ct);
        await events.Publish(PosEventNames.MpesaReceived, new { id }, ct);
        var date = BusinessDates.Of(transaction.At, await BusinessDates.Zone(db, ct));
        return (await List(new MpesaLedgerFilters { From = date, To = date }, ct)).Rows.Single(x => x.Id == id);
    }

    /// <summary>Fetch payments now: a catch-up collection from the relay.</summary>
    public async Task<int> FetchNow(CancellationToken ct)
    {
        await RequireView(ct);
        try
        {
            return await catchUp.Run(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or DarajaException or InvalidOperationException)
        {
            throw new DomainException($"Can't reach the relay: {e.Message}", MpesaCodes.Unavailable);
        }
    }

    /// <summary>The Excel export (for checking against the Safaricom till statement), in the Task 13 format.</summary>
    public async Task<ReportFile> Export(MpesaLedgerFilters f, CancellationToken ct)
    {
        var list = await List(f, ct);
        var zone = await BusinessDates.Zone(db, ct);
        var columns = ExportColumns;
        var rows = list.Rows.Select(r => new ReportRow([
            r.At, r.Code, r.PayerName, r.Phone, r.How, r.Amount, r.Amount - r.Available, r.Available, StatusText(r),
            string.Join(", ", r.Uses.Select(u => $"#{u.TicketNumber}")), string.Join(", ", r.Uses.Where(u => u.Receipt is not null).Select(u => u.Receipt!.Value.ToString(CultureInfo.InvariantCulture))),
            string.Join(", ", r.Uses.Select(u => u.Cashier).OfType<string>().Distinct()), r.Note,
        ])).ToList();
        var result = new ReportResult("mpesa", "M-Pesa payments", list.Period, [], clock.GetUtcNow(),
            [new ReportTable(null, columns, [new ReportGroup(null, rows)],
                ReportRow.Of(null, null, null, null, "Total", list.Rows.Sum(r => r.Amount), list.Rows.Sum(r => r.Amount - r.Available), list.Rows.Sum(r => r.Available)))],
            []);
        return new ReportFile(ReportExcel.Build(result, zone), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"mpesa-{TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone):yyyyMMdd-HHmm}.xlsx");
    }

    public static readonly IReadOnlyList<ReportColumn> ExportColumns =
    [
        new("time", "Time", ColumnType.DateTime), new("code", "Code"), new("from", "From"), new("phone", "Phone"), new("how", "How"),
        new("amount", "Amount", ColumnType.Money), new("applied", "Applied", ColumnType.Money), new("unapplied", "Unapplied", ColumnType.Money),
        new("status", "Status"), new("tickets", "Tickets"), new("receipts", "Receipts"), new("cashier", "Cashier"), new("note", "Note"),
    ];

    public static string StatusText(MpesaLedgerRow r) => r.Status switch
    {
        "NotFromPos" => "Not from POS",
        "NotConfirmed" => r.NeverConfirmed ? "Typed, never confirmed" : "Typed, not confirmed yet",
        "PartlyApplied" => "Partly applied",
        _ => r.Status,
    };

    private async Task RequireView(CancellationToken ct)
    {
        var user = session.RequireUser();
        if (await permissions.Check(user, PermissionKeys.ReportsAll, ct) == PermissionValue.Allowed) return;
        if (await permissions.Check(user, PermissionKeys.CashBook, ct) == PermissionValue.Allowed) return;
        throw new PermissionDeniedException(PermissionKeys.ReportsAll);
    }
}
