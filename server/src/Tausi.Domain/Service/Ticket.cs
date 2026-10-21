using Tausi.Domain.Common;

namespace Tausi.Domain.Service;

public enum DiscountKind
{
    Percent,
    Amount,
}

public enum TicketStatus
{
    Open,
    Settled,
    Merged,
    Cancelled,
}

/// <summary>Who holds a ticket's edit lock.</summary>
public sealed record TicketLock(Guid TerminalId, Guid UserId, Guid SessionId, DateTimeOffset ExpiresAt);

/// <summary>
/// A bill for a table (or a takeaway customer). It is saved with its first line, never empty, and
/// gets its number then. Its total is always the sum of its lines; nothing stores a total.
/// While it is on screen on a terminal it carries an edit lock, renewed by that terminal.
/// </summary>
public class Ticket : VersionedEntity
{
    public const int MaxCustomerNameLength = 60;
    public const int MaxNoteLength = 1000;

    /// <summary>A lock not renewed for this long is gone. Terminals renew every 30 seconds.</summary>
    public static readonly TimeSpan LockLifetime = TimeSpan.FromSeconds(90);

    public long Number { get; set; }
    public Guid? TableId { get; set; }
    public DiningTable? Table { get; set; }
    public string? CustomerName { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public Guid OpenedByUserId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public Guid TerminalId { get; set; }
    public Guid WorkPeriodId { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset? BillPrintedAt { get; set; }
    public string Note { get; set; } = "";
    public Guid? MergedIntoTicketId { get; set; }

    /// <summary>Bills printed so far ("Bill 1", "Bill 2", …; reprints count too).</summary>
    public int BillNumber { get; set; }
    /// <summary>Print rounds so far: each sends the New lines to the dispense printer.</summary>
    public int PrintRound { get; set; }
    /// <summary>The print round the last bill covered; a bill again with nothing printed since is a reprint.</summary>
    public int BilledRound { get; set; }

    public Guid? LockTerminalId { get; set; }
    public Guid? LockUserId { get; set; }
    public Guid? LockSessionId { get; set; }
    public DateTimeOffset? LockExpiresAt { get; set; }

    /// <summary>The ticket discount (one per ticket): a percent, or an amount in the currency.</summary>
    public DiscountKind? DiscountKind { get; set; }
    public decimal DiscountValue { get; set; }
    /// <summary>"Regular customer", "Promotion", "Compensation" or "Other: …", printed on the bill.</summary>
    public string? DiscountReason { get; set; }

    /// <summary>Set when settled: who, where and the receipt number (a global sequence of its own).</summary>
    public DateTimeOffset? SettledAt { get; set; }
    public Guid? SettledByUserId { get; set; }
    public Guid? SettledTerminalId { get; set; }
    public long? ReceiptNumber { get; set; }

    /// <summary>
    /// Due minus total under the rounding setting (e.g. -0.40 when 1,370.40 is due as 1,370). Kept
    /// up to date while settling, and final when settled.
    /// </summary>
    public decimal RoundingAmount { get; set; }

    public List<OrderLine> Lines { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];

    /// <summary>What the customer pays: the total after rounding.</summary>
    public decimal AmountDue => Total + RoundingAmount;

    public decimal Paid => Payments.Where(p => p.Counts).Sum(p => p.Amount);

    public decimal Remaining => AmountDue - Paid;

    /// <summary>Works out <see cref="RoundingAmount"/> from the current total and the business's rounding rule.</summary>
    public void ApplyRounding(Money.RoundingRule rule) => RoundingAmount = Money.CashRounding.Apply(Total, rule).Difference;

    public bool IsOpen => Status == TicketStatus.Open;

    /// <summary>What the customer pays: the lines after their discount shares.</summary>
    public decimal Total => TotalOf(Lines);

    /// <summary>The lines before the discount.</summary>
    public decimal Subtotal => Lines.Where(l => l.Counts).Sum(l => l.Gross);

    public decimal DiscountTotal => Lines.Where(l => l.Counts).Sum(l => l.Discount);

    public static decimal TotalOf(IEnumerable<OrderLine> lines) => lines.Where(l => l.Counts).Sum(l => l.Payable);

    /// <summary>The lines a discount applies to: counted, not gifts.</summary>
    public IEnumerable<OrderLine> DiscountableLines => Lines.Where(l => l.Counts && !l.IsGift && l.Gross > 0);

    /// <summary>Sets (or with null, removes) the discount and spreads it over the lines.</summary>
    public void SetDiscount(DiscountKind? kind, decimal value, string? reason)
    {
        if (kind is null)
        {
            DiscountKind = null;
            DiscountValue = 0;
            DiscountReason = null;
        }
        else
        {
            var basis = DiscountableLines.Sum(l => l.Gross);
            if (value <= 0) throw new DomainException("The discount must be more than 0.");
            if (kind == Service.DiscountKind.Percent && value > 100) throw new DomainException("A discount can be at most 100%.");
            if (kind == Service.DiscountKind.Amount && value > basis)
                throw new DomainException($"The discount can't be more than the total of {basis:#,##0.##}.");
            DiscountKind = kind;
            DiscountValue = value;
            DiscountReason = reason;
        }
        SpreadDiscount();
    }

    /// <summary>
    /// Spreads the discount over the lines in proportion to their amounts, so VAT stays right per
    /// rate. Each share is rounded to the cent and the rounding leftover goes on the largest line,
    /// so the shares add up exactly. Called again whenever lines change. An amount larger than
    /// what is left (after a void) is capped at it.
    /// </summary>
    public void SpreadDiscount()
    {
        // Amounts first (a price or quantity may just have changed), then the shares on them.
        foreach (var line in Lines)
        {
            line.Discount = 0;
            line.Recalculate();
        }
        var lines = DiscountableLines.ToList();
        var basis = lines.Sum(l => l.Gross);
        var target = DiscountKind switch
        {
            Service.DiscountKind.Percent => Money.MoneyMath.Round(basis * DiscountValue / 100m),
            Service.DiscountKind.Amount => Math.Min(DiscountValue, basis),
            _ => 0m,
        };
        var shares = DiscountSpread.Spread(target, lines.Select(l => l.Gross).ToList());
        for (var i = 0; i < lines.Count; i++) lines[i].Discount = shares[i];
        foreach (var line in Lines) line.Recalculate();
    }

    public int NewLineCount => Lines.Count(l => l.Status == OrderLineStatus.New);

    /// <summary>The lock as stored, if it has not run out. Whether its session still lives is checked by the caller.</summary>
    public TicketLock? LockAt(DateTimeOffset now) =>
        LockTerminalId is { } terminal && LockUserId is { } user && LockSessionId is { } session
            && LockExpiresAt is { } expires && expires > now
            ? new TicketLock(terminal, user, session, expires)
            : null;

    public void TakeLock(Guid terminalId, Guid userId, Guid sessionId, DateTimeOffset now)
    {
        LockTerminalId = terminalId;
        LockUserId = userId;
        LockSessionId = sessionId;
        LockExpiresAt = now + LockLifetime;
    }

    public void ClearLock()
    {
        LockTerminalId = null;
        LockUserId = null;
        LockSessionId = null;
        LockExpiresAt = null;
    }

    public void Touch(DateTimeOffset now) => LastActivityAt = now;

    /// <summary>
    /// Moves the other tickets' lines here unchanged (who added them, terminal, time, printed state
    /// and price stay as they were), marks those tickets Merged and joins the notes.
    /// </summary>
    public void MergeIn(IReadOnlyList<Ticket> others, DateTimeOffset now)
    {
        if (!IsOpen) throw new DomainException($"#{Number} is not open.");
        foreach (var other in others)
        {
            if (other.Id == Id) throw new DomainException("A ticket can't be merged into itself.");
            if (!other.IsOpen) throw new DomainException($"#{other.Number} is not open.");
            if (other.TableId != TableId) throw new DomainException("Only tickets on the same table can be merged.");
            if (other.DiscountKind is not null)
                throw new DomainException($"#{other.Number} has its own discount. Remove it first, then merge.");
        }

        foreach (var other in others)
        {
            foreach (var line in other.Lines.ToList())
            {
                other.Lines.Remove(line);
                line.TicketId = Id;
                Lines.Add(line);
            }
            other.Status = TicketStatus.Merged;
            other.MergedIntoTicketId = Id;
            other.ClearLock();
            other.Touch(now);
        }

        // The kept ticket's discount (if any) now covers the merged-in lines too.
        SpreadDiscount();

        var notes = new[] { this }.Concat(others).Select(t => t.Note.Trim()).Where(n => n.Length > 0);
        Note = string.Join(" / ", notes);
        if (Note.Length > MaxNoteLength) Note = Note[..MaxNoteLength];
        Touch(now);
    }
}
