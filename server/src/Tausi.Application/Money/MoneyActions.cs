using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Approvals;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Ledger;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Stock;

namespace Tausi.Application.Money;

public sealed record ExpenseRequest(
    DateOnly? Date, decimal Amount, Guid CategoryId, MoneyAccount PaidFrom, string? Payee, string? Reference, string? Note, Guid? AttachmentFileId);

public sealed record SupplierPaymentRequest(
    Guid SupplierId, decimal Amount, MoneyAccount PaidFrom, string? Reference, DateOnly? Date, List<Guid>? DeliveryIds);

public sealed record MoveMoneyRequest(MoneyAccount From, MoneyAccount To, decimal Amount, decimal? Fee, string? Reference, DateOnly? Date);

/// <param name="TookOut">True: the owner took money (drawings). False: the owner put money in (capital).</param>
public sealed record OwnerMoneyRequest(bool TookOut, MoneyAccount Account, decimal Amount, string? Note, DateOnly? Date);

public sealed record OtherIncomeRequest(MoneyAccount To, decimal Amount, string? Note, string? Payee, DateOnly? Date);

public sealed record OpeningSupplier(Guid SupplierId, decimal Amount);

public sealed record OpeningBalancesRequest(decimal? Safe, decimal? Bank, decimal? Mpesa, List<OpeningSupplier>? Suppliers, DateOnly? Date);

public sealed record ReverseMoneyRequest(string Reason);

public sealed record RejectMoneyRequest(string? Note);

public sealed record MoneyDocumentDto(
    Guid Id, string Kind, string Title, string Status, DateOnly BusinessDate, decimal Amount, decimal Fee, string? From, string? To,
    string? Category, Guid? SupplierId, string? Supplier, string? Payee, string? Reference, string? Note, bool HasAttachment,
    string CreatedBy, DateTimeOffset CreatedAt, string? ApprovedBy, string? RejectNote, string? ReversalStatus, string? ReversalReason,
    Guid? JournalEntryId, Guid? ReversalEntryId, string? Warning, IReadOnlyList<OpeningLine> OpeningLines);

public static class MoneyCodes
{
    public const string Overpaid = "overpaid";
    public const string OpeningLocked = "opening_locked";
    public const string AlreadyReversed = "already_reversed";
}

/// <summary>What each supplier is owed: credit deliveries + opening balances − payments (all standing).</summary>
public static class SupplierBalances
{
    public static async Task<Dictionary<Guid, decimal>> Owed(ITausiDb db, CancellationToken ct)
    {
        var owed = await db.Deliveries.AsNoTracking()
            .Where(d => d.Payment == DeliveryPayment.OnCredit && d.Status == StockDocumentStatus.Posted
                && (d.ReversalStatus == null || d.ReversalStatus != StockDocumentStatus.Posted))
            .GroupBy(d => d.SupplierId).Select(g => new { g.Key, Total = g.Sum(d => d.Total) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);
        var documents = await db.MoneyDocuments.AsNoTracking()
            .Where(d => (d.Kind == MoneyDocumentKind.SupplierPayment || d.Kind == MoneyDocumentKind.OpeningBalances)
                && d.Status == MoneyDocumentStatus.Posted && (d.ReversalStatus == null || d.ReversalStatus != MoneyDocumentStatus.Posted))
            .ToListAsync(ct);
        foreach (var d in documents)
        {
            if (d.Kind == MoneyDocumentKind.SupplierPayment && d.SupplierId is { } paid)
                owed[paid] = owed.GetValueOrDefault(paid) - d.Amount;
            foreach (var line in d.OpeningLines.Where(l => l.SupplierId != null))
                owed[line.SupplierId!.Value] = owed.GetValueOrDefault(line.SupplierId.Value) + line.Amount;
        }
        return owed;
    }
}

/// <summary>
/// The admin site's money actions. Each posts one balanced journal entry dated as chosen (or waits
/// as Pending when the key needs approval), is audited, and is never edited: Reverse posts the exact
/// opposite, linked both ways. Expenses need <c>cash.expense</c>; opening balances
/// <c>settings.manage</c>; the rest <c>cash.book</c>.
/// </summary>
public sealed class MoneyActions(
    ITausiDb db, ICurrentSession session, AdminApprovals approvals, IPermissionService permissions, IApprovalTokenSource approval,
    ILedgerPostingService ledger, IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public const string LedgerSource = "Money";
    public const string Event = "money.changed";

    public static string AccountCode(MoneyAccount account) => account switch
    {
        MoneyAccount.Safe => LedgerAccountCodes.Safe,
        MoneyAccount.Bank => LedgerAccountCodes.Bank,
        _ => LedgerAccountCodes.MPesa,
    };

    public static string AccountName(MoneyAccount account) => account switch
    {
        MoneyAccount.Safe => "Safe",
        MoneyAccount.Bank => "Bank",
        _ => "M-Pesa",
    };

    public static string KeyFor(MoneyDocumentKind kind) => kind switch
    {
        MoneyDocumentKind.Expense => PermissionKeys.CashExpense,
        MoneyDocumentKind.OpeningBalances => PermissionKeys.SettingsManage,
        _ => PermissionKeys.CashBook,
    };

    public async Task<MoneyDocumentDto> AddExpense(ExpenseRequest r, CancellationToken ct)
    {
        if (!await db.ExpenseCategories.AnyAsync(c => c.Id == r.CategoryId && c.Active, ct)) throw new DomainException("Choose what the expense was for.");
        if (r.AttachmentFileId is { } file && !await db.StoredFiles.AnyAsync(f => f.Id == file && f.IsPrivate, ct))
            throw new DomainException("The receipt photo wasn't found. Upload it again.");
        return await Record(new MoneyDocument
        {
            Kind = MoneyDocumentKind.Expense, Amount = Amount(r.Amount), From = r.PaidFrom, CategoryId = r.CategoryId,
            Payee = Text(r.Payee), Reference = Text(r.Reference), Note = Note(r.Note), AttachmentFileId = r.AttachmentFileId,
        }, r.Date, ct);
    }

    public async Task<MoneyDocumentDto> PaySupplier(SupplierPaymentRequest r, CancellationToken ct)
    {
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.SupplierId, ct) ?? throw new DomainException("Choose the supplier.");
        var amount = Amount(r.Amount);
        var owed = (await SupplierBalances.Owed(db, ct)).GetValueOrDefault(supplier.Id);
        if (amount > owed)
            throw new DomainException($"{supplier.Name} is owed {Format(owed)}. Pay {Format(owed)} or less.", MoneyCodes.Overpaid);
        return await Record(new MoneyDocument
        {
            Kind = MoneyDocumentKind.SupplierPayment, Amount = amount, From = r.PaidFrom, SupplierId = supplier.Id,
            Reference = Text(r.Reference), DeliveryIds = r.DeliveryIds ?? [],
        }, r.Date, ct);
    }

    public async Task<MoneyDocumentDto> MoveMoney(MoveMoneyRequest r, CancellationToken ct)
    {
        if (r.From == r.To) throw new DomainException("Choose two different places.");
        var fee = r.Fee ?? 0;
        if (fee < 0 || decimal.Round(fee, 2) != fee) throw new DomainException("The fee can't be below 0.");
        return await Record(new MoneyDocument
        {
            Kind = MoneyDocumentKind.MoveMoney, Amount = Amount(r.Amount), Fee = fee, From = r.From, To = r.To, Reference = Text(r.Reference),
        }, r.Date, ct);
    }

    public Task<MoneyDocumentDto> OwnerMoney(OwnerMoneyRequest r, CancellationToken ct) =>
        Record(new MoneyDocument
        {
            Kind = r.TookOut ? MoneyDocumentKind.OwnerDrawing : MoneyDocumentKind.OwnerCapital, Amount = Amount(r.Amount),
            From = r.TookOut ? r.Account : null, To = r.TookOut ? null : r.Account, Note = Note(r.Note),
        }, r.Date, ct);

    public Task<MoneyDocumentDto> OtherIncome(OtherIncomeRequest r, CancellationToken ct)
    {
        var note = Note(r.Note) ?? throw new DomainException("Say what the money was for.", "note_required");
        return Record(new MoneyDocument
        {
            Kind = MoneyDocumentKind.OtherIncome, Amount = Amount(r.Amount), To = r.To, Note = note, Payee = Text(r.Payee),
        }, r.Date, ct);
    }

    /// <summary>Once only (settings.manage): the starting Safe, Bank and M-Pesa and what each supplier was owed. Locked after.</summary>
    public async Task<MoneyDocumentDto> OpeningBalances(OpeningBalancesRequest r, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);
        if (await OpeningLocked(ct))
            throw new ConflictException("Opening balances are already entered. Reverse them first to enter them again.", MoneyCodes.OpeningLocked);
        var lines = new List<OpeningLine>();
        foreach (var (account, amount) in new[] { (MoneyAccount.Safe, r.Safe), (MoneyAccount.Bank, r.Bank), (MoneyAccount.Mpesa, r.Mpesa) })
            if (amount is { } a && a != 0) lines.Add(new OpeningLine(account, null, Amount(a)));
        foreach (var s in r.Suppliers ?? [])
        {
            if (s.Amount == 0) continue;
            if (!await db.Suppliers.AnyAsync(x => x.Id == s.SupplierId, ct)) throw new DomainException("That supplier no longer exists.");
            lines.Add(new OpeningLine(null, s.SupplierId, Amount(s.Amount)));
        }
        if (lines.Count == 0) throw new DomainException("Enter at least one balance.");
        var document = new MoneyDocument { Kind = MoneyDocumentKind.OpeningBalances, Amount = lines.Sum(l => l.Amount), OpeningLines = lines };
        return await Save(document, r.Date, postNow: true, grant.ApprovedByUserId, ct);
    }

    public async Task<bool> OpeningLocked(CancellationToken ct) =>
        await db.MoneyDocuments.AnyAsync(d => d.Kind == MoneyDocumentKind.OpeningBalances && d.Status == MoneyDocumentStatus.Posted
            && (d.ReversalStatus == null || d.ReversalStatus != MoneyDocumentStatus.Posted), ct);

    public async Task<MoneyDocumentDto> Approve(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var document = await Load(id, ct);
        if (document.Status == MoneyDocumentStatus.Pending)
        {
            await approvals.RequireApprover(user, KeyFor(document.Kind), document.CreatedByUserId, ct);
            await Post(document, user.UserId, ct);
        }
        else if (document.ReversalStatus == MoneyDocumentStatus.Pending)
        {
            await approvals.RequireApprover(user, PermissionKeys.CashBook, document.ReversalRequestedByUserId!.Value, ct);
            await PostReversal(document, user.UserId, ct);
        }
        else throw new ConflictException("Nothing on this is waiting for approval.", AdminApprovals.NotPending);
        audit.Write(new AuditRecord(AuditActions.MoneyApproved, nameof(MoneyDocument), id.ToString(), null, new { kind = document.Kind.ToString() }, user.UserId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await events.Publish(Event, new { }, ct);
        return (await View([id], ct)).Single();
    }

    public async Task<MoneyDocumentDto> Reject(Guid id, RejectMoneyRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        var document = await Load(id, ct);
        if (document.Status == MoneyDocumentStatus.Pending)
        {
            await approvals.RequireApprover(user, KeyFor(document.Kind), document.CreatedByUserId, ct);
            document.Status = MoneyDocumentStatus.Rejected;
        }
        else if (document.ReversalStatus == MoneyDocumentStatus.Pending)
        {
            await approvals.RequireApprover(user, PermissionKeys.CashBook, document.ReversalRequestedByUserId!.Value, ct);
            document.ReversalStatus = MoneyDocumentStatus.Rejected;
        }
        else throw new ConflictException("Nothing on this is waiting for approval.", AdminApprovals.NotPending);
        document.RejectNote = Note(r.Note);
        audit.Write(new AuditRecord(AuditActions.MoneyRejected, nameof(MoneyDocument), id.ToString(), null, new { note = document.RejectNote }, user.UserId));
        await db.SaveChangesAsync(ct);
        await events.Publish(Event, new { }, ct);
        return (await View([id], ct)).Single();
    }

    /// <summary>The exact opposite entry, dated today, linked both ways (cash.book; Pending when it needs approval). Once only.</summary>
    public async Task<MoneyDocumentDto> Reverse(Guid id, ReverseMoneyRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        var postNow = await approvals.PostsNow(user, PermissionKeys.CashBook, ct);
        var reason = Note(r.Reason) ?? throw new DomainException("Say why it is reversed.", "reason_required");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var document = await Load(id, ct);
        if (document.Status != MoneyDocumentStatus.Posted) throw new ConflictException("Only a recorded entry can be reversed.", AdminApprovals.NotPending);
        if (document.ReversalStatus is MoneyDocumentStatus.Posted or MoneyDocumentStatus.Pending)
            throw new ConflictException("This was reversed already.", MoneyCodes.AlreadyReversed);
        document.ReversalReason = reason;
        document.ReversalRequestedByUserId = user.UserId;
        document.ReversalStatus = MoneyDocumentStatus.Pending;
        document.RejectNote = null;
        if (postNow) await PostReversal(document, user.UserId, ct);
        audit.Write(new AuditRecord(AuditActions.MoneyReversed, nameof(MoneyDocument), id.ToString(), null, new { reason, pending = !postNow }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await events.Publish(Event, new { }, ct);
        return (await View([id], ct)).Single();
    }

    public async Task<IReadOnlyList<MoneyDocumentDto>> View(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var documents = await db.MoneyDocuments.AsNoTracking().Where(d => ids.Contains(d.Id)).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var categories = await db.ExpenseCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return ids.Select(id => documents.FirstOrDefault(d => d.Id == id)).OfType<MoneyDocument>().Select(d => new MoneyDocumentDto(
            d.Id, d.Kind.ToString(), Title(d, categories, suppliers), d.Status.ToString(), d.BusinessDate, d.Amount, d.Fee,
            d.From?.ToString(), d.To?.ToString(), d.CategoryId is { } c ? categories.GetValueOrDefault(c) : null, d.SupplierId,
            d.SupplierId is { } s ? suppliers.GetValueOrDefault(s) : null, d.Payee, d.Reference, d.Note, d.AttachmentFileId != null,
            users.GetValueOrDefault(d.CreatedByUserId, "?"), d.CreatedAt, d.ApprovedByUserId is { } a ? users.GetValueOrDefault(a) : null,
            d.RejectNote, d.ReversalStatus?.ToString(), d.ReversalReason, d.JournalEntryId, d.ReversalEntryId, null, d.OpeningLines)).ToList();
    }

    /// <summary>The words the cash book shows for it: "Paid KBL", "Banked cash", "Ice and charcoal (expense)".</summary>
    public static string Title(MoneyDocument d, IReadOnlyDictionary<Guid, string> categories, IReadOnlyDictionary<Guid, string> suppliers) => d.Kind switch
    {
        MoneyDocumentKind.Expense => $"{d.Payee ?? d.Note ?? (d.CategoryId is { } c ? categories.GetValueOrDefault(c) : null) ?? "Expense"} (expense)",
        MoneyDocumentKind.SupplierPayment => $"Paid {(d.SupplierId is { } s ? suppliers.GetValueOrDefault(s, "supplier") : "supplier")}",
        MoneyDocumentKind.MoveMoney => (d.From, d.To) switch
        {
            (MoneyAccount.Safe, MoneyAccount.Bank) => "Banked cash",
            (MoneyAccount.Mpesa, MoneyAccount.Bank) => "M-Pesa to bank",
            (MoneyAccount.Bank, MoneyAccount.Safe) => "Withdrew from the bank",
            (MoneyAccount.Mpesa, MoneyAccount.Safe) => "M-Pesa to the safe",
            _ => $"Moved money · {AccountName(d.From!.Value)} to {AccountName(d.To!.Value)}",
        },
        MoneyDocumentKind.OwnerDrawing => "Owner took money",
        MoneyDocumentKind.OwnerCapital => "Owner put money in",
        MoneyDocumentKind.OtherIncome => $"Other income · {d.Note}",
        _ => "Opening balances",
    };

    private async Task<MoneyDocumentDto> Record(MoneyDocument document, DateOnly? date, CancellationToken ct)
    {
        var user = session.RequireUser();
        var postNow = await approvals.PostsNow(user, KeyFor(document.Kind), ct);
        return await Save(document, date, postNow, null, ct);
    }

    private async Task<MoneyDocumentDto> Save(MoneyDocument document, DateOnly? date, bool postNow, Guid? approvedBy, CancellationToken ct)
    {
        var user = session.RequireUser();
        document.BusinessDate = await BusinessDates.Chosen(db, clock, date, ct);
        document.Status = MoneyDocumentStatus.Pending;
        document.CreatedByUserId = user.UserId;
        document.CreatedAt = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.MoneyDocuments.Add(document);
        audit.Write(new AuditRecord(AuditActions.MoneyRecorded, nameof(MoneyDocument), document.Id.ToString(), null,
            new { kind = document.Kind.ToString(), date = document.BusinessDate, document.Amount, document.Fee, from = document.From?.ToString(),
                to = document.To?.ToString(), document.CategoryId, document.SupplierId, document.Payee, document.Reference, document.Note, pending = !postNow },
            approvedBy));
        if (postNow) await Post(document, null, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await events.Publish(Event, new { }, ct);
        var dto = (await View([document.Id], ct)).Single();
        return dto with { Warning = await Warning(document, ct) };
    }

    /// <summary>Moving more than a place holds is allowed (the books may be behind) but said.</summary>
    private async Task<string?> Warning(MoneyDocument d, CancellationToken ct)
    {
        if (d.From is not { } from || d.Kind is MoneyDocumentKind.OpeningBalances) return null;
        var balance = await LedgerBalances.Balance(db, AccountCode(from), d.BusinessDate, ct);
        return balance < 0 ? $"The {AccountName(from)} is now below 0 in the books ({Format(balance)}). Check for a missing entry." : null;
    }

    private async Task Post(MoneyDocument d, Guid? approvedBy, CancellationToken ct)
    {
        var lines = new List<JournalLineDraft>();
        switch (d.Kind)
        {
            case MoneyDocumentKind.Expense:
                var category = await db.ExpenseCategories.Where(c => c.Id == d.CategoryId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "Other";
                lines.Add(JournalLineDraft.Dr(LedgerAccountCodes.Expenses, d.Amount, category, d.CategoryId));
                lines.Add(JournalLineDraft.Cr(AccountCode(d.From!.Value), d.Amount, d.Payee ?? category));
                break;
            case MoneyDocumentKind.SupplierPayment:
                lines.Add(JournalLineDraft.Dr(LedgerAccountCodes.SupplierPayables, d.Amount, "Paid supplier"));
                lines.Add(JournalLineDraft.Cr(AccountCode(d.From!.Value), d.Amount, "Paid supplier"));
                break;
            case MoneyDocumentKind.MoveMoney:
                lines.Add(JournalLineDraft.Dr(AccountCode(d.To!.Value), d.Amount, "Moved in"));
                lines.Add(JournalLineDraft.Cr(AccountCode(d.From!.Value), d.Amount, "Moved out"));
                if (d.Fee > 0)
                {
                    lines.Add(JournalLineDraft.Dr(LedgerAccountCodes.BankCharges, d.Fee, "Fee"));
                    lines.Add(JournalLineDraft.Cr(AccountCode(d.From!.Value), d.Fee, "Fee"));
                }
                break;
            case MoneyDocumentKind.OwnerDrawing:
                lines.Add(JournalLineDraft.Dr(LedgerAccountCodes.OwnersDrawings, d.Amount, "Owner took money"));
                lines.Add(JournalLineDraft.Cr(AccountCode(d.From!.Value), d.Amount, "Owner took money"));
                break;
            case MoneyDocumentKind.OwnerCapital:
                lines.Add(JournalLineDraft.Dr(AccountCode(d.To!.Value), d.Amount, "Owner put money in"));
                lines.Add(JournalLineDraft.Cr(LedgerAccountCodes.OwnersCapital, d.Amount, "Owner put money in"));
                break;
            case MoneyDocumentKind.OtherIncome:
                lines.Add(JournalLineDraft.Dr(AccountCode(d.To!.Value), d.Amount, d.Note ?? "Other income"));
                lines.Add(JournalLineDraft.Cr(LedgerAccountCodes.OtherIncome, d.Amount, d.Note ?? "Other income"));
                break;
            case MoneyDocumentKind.OpeningBalances:
                foreach (var l in d.OpeningLines.Where(l => l.Account != null))
                    AddSigned(lines, AccountCode(l.Account!.Value), LedgerAccountCodes.OpeningBalances, l.Amount, "Opening balance");
                foreach (var l in d.OpeningLines.Where(l => l.SupplierId != null))
                    AddSigned(lines, LedgerAccountCodes.OpeningBalances, LedgerAccountCodes.SupplierPayables, l.Amount, "Owed at the start");
                break;
        }
        var entry = await ledger.Post(new JournalEntryDraft(clock.GetUtcNow(), await TitleOf(d, ct), LedgerSource, d.Id.ToString(), lines,
            BusinessDate: d.BusinessDate), ct);
        d.JournalEntryId = entry.Id;
        d.Status = MoneyDocumentStatus.Posted;
        d.ApprovedByUserId = approvedBy;
        d.PostedAt = clock.GetUtcNow();
    }

    private static void AddSigned(List<JournalLineDraft> lines, string debit, string credit, decimal amount, string memo)
    {
        if (amount >= 0)
        {
            lines.Add(JournalLineDraft.Dr(debit, amount, memo));
            lines.Add(JournalLineDraft.Cr(credit, amount, memo));
        }
        else
        {
            lines.Add(JournalLineDraft.Dr(credit, -amount, memo));
            lines.Add(JournalLineDraft.Cr(debit, -amount, memo));
        }
    }

    private async Task PostReversal(MoneyDocument d, Guid userId, CancellationToken ct)
    {
        var original = await db.JournalEntries.AsNoTracking().Include(e => e.Lines).ThenInclude(l => l.Account)
            .FirstAsync(e => e.Id == d.JournalEntryId, ct);
        if (original.ReversedByEntryId != null) throw new ConflictException("This was reversed already.", MoneyCodes.AlreadyReversed);
        var lines = original.Lines.Select(l => new JournalLineDraft(l.Account!.Code, l.Credit, l.Debit, l.Memo, l.ExpenseCategoryId)).ToList();
        var entry = await ledger.Post(new JournalEntryDraft(clock.GetUtcNow(), $"Reversed: {original.Memo}", LedgerSource, d.Id + ":reversal", lines,
            BusinessDate: await BusinessDates.Today(db, clock, ct), ReversesEntryId: original.Id), ct);
        d.ReversalEntryId = entry.Id;
        d.ReversalStatus = MoneyDocumentStatus.Posted;
        d.ReversalApprovedByUserId = userId == d.ReversalRequestedByUserId ? null : userId;
        d.ReversedAt = clock.GetUtcNow();
    }

    private async Task<string> TitleOf(MoneyDocument d, CancellationToken ct) =>
        Title(d, await db.ExpenseCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct),
            await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct));

    private async Task<MoneyDocument> Load(Guid id, CancellationToken ct) =>
        await db.MoneyDocuments.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Not found.");

    private static decimal Amount(decimal amount)
    {
        if (amount <= 0) throw new DomainException("Enter an amount above 0.", "amount_invalid");
        if (decimal.Round(amount, 2) != amount) throw new DomainException("Use at most 2 decimal places.", "amount_invalid");
        return amount;
    }

    private static string? Text(string? text)
    {
        var t = text?.Trim();
        if (t is { Length: > MoneyDocument.MaxTextLength }) throw new DomainException($"Keep it under {MoneyDocument.MaxTextLength} characters.");
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private static string? Note(string? text)
    {
        var t = text?.Trim();
        if (t is { Length: > MoneyDocument.MaxNoteLength }) throw new DomainException($"Keep the note under {MoneyDocument.MaxNoteLength} characters.");
        return string.IsNullOrEmpty(t) ? null : t;
    }

    public static string Format(decimal amount) => amount.ToString("#,##0.##", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Account balances from the journal lines, as at the end of a business date.</summary>
public static class LedgerBalances
{
    /// <summary>Debits − credits up to and including the date.</summary>
    public static async Task<decimal> Balance(ITausiDb db, string accountCode, DateOnly upTo, CancellationToken ct) =>
        await db.JournalLines.AsNoTracking()
            .Where(l => l.Account!.Code == accountCode)
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { l.Debit, l.Credit, e.BusinessDate })
            .Where(x => x.BusinessDate <= upTo)
            .SumAsync(x => x.Debit - x.Credit, ct);
}
