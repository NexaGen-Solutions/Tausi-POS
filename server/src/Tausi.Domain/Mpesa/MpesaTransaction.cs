using Tausi.Domain.Common;

namespace Tausi.Domain.Mpesa;

/// <summary>How a payment reached the POS.</summary>
public enum MpesaSource
{
    /// <summary>The customer approved a prompt sent to their phone.</summary>
    Stk,

    /// <summary>Paid to the till or paybill (by hand or by scanning a bill's QR); Safaricom told the relay.</summary>
    C2B,

    /// <summary>A cashier typed the code from the customer's SMS; unconfirmed until Safaricom's copy arrives.</summary>
    Typed,
}

public enum MpesaStatus
{
    Unapplied,
    PartlyApplied,
    Applied,

    /// <summary>Money that wasn't for a bill (an owner's deposit, a refund): posted to another account.</summary>
    NotFromPos,
}

/// <summary>
/// One M-Pesa payment the business received (or a code a cashier typed), keyed on the M-Pesa
/// transaction id. It is applied to bills as pending payments, up to its amount and never more than
/// a bill's remaining amount; what is left stays for another bill. It never gives change.
/// </summary>
public class MpesaTransaction : VersionedEntity
{
    public const int CodeLength = 10;
    public const int MaxNameLength = 120;
    public const int MaxPhoneLength = 40;
    public const int MaxReferenceLength = 40;
    public const int MaxReasonLength = 300;

    /// <summary>Paybill account numbers and QR references are ticket numbers ("1042" or "#1042").</summary>
    public string TransactionId { get; set; } = "";

    public decimal Amount { get; set; }
    public decimal AmountApplied { get; set; }
    public string? PayerName { get; set; }

    /// <summary>Masked ("0712 ••• 678").</summary>
    public string? Phone { get; set; }

    /// <summary>When it was paid (Safaricom's time), or typed.</summary>
    public DateTimeOffset At { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
    public string? ShortCode { get; set; }

    /// <summary>The account number (Paybill) or the QR's reference.</summary>
    public string? BillReference { get; set; }

    public MpesaSource Source { get; set; }
    public MpesaStatus Status { get; set; } = MpesaStatus.Unapplied;

    /// <summary>Safaricom sent it (STK, C2B, or a typed code later matched). Typed codes start unconfirmed.</summary>
    public bool Confirmed { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>A typed code: who typed it.</summary>
    public Guid? TypedByUserId { get; set; }

    /// <summary>The ticket its reference matched exactly (QR or Paybill account), if any.</summary>
    public Guid? MatchedTicketId { get; set; }

    /// <summary>The STK prompt it answered.</summary>
    public Guid? PromptId { get; set; }

    public string? NotFromPosReason { get; set; }

    /// <summary>The ledger account it was posted to when not from the POS (Other income or Owner's capital).</summary>
    public string? NotFromPosAccount { get; set; }

    public Guid? NotFromPosByUserId { get; set; }
    public DateTimeOffset? NotFromPosAt { get; set; }

    public decimal Available => Status == MpesaStatus.NotFromPos ? 0 : Amount - AmountApplied;

    /// <summary>Applies part (or all) of it; returns how much was used.</summary>
    public decimal Apply(decimal wanted)
    {
        if (wanted <= 0) throw new DomainException("Choose an amount above 0.");
        var used = Math.Min(wanted, Available);
        if (used <= 0) throw new DomainException($"{TransactionId} has been used in full.", MpesaCodes.UsedUp);
        AmountApplied += used;
        Status = AmountApplied >= Amount ? MpesaStatus.Applied : MpesaStatus.PartlyApplied;
        return used;
    }

    /// <summary>A payment that used it was taken off its bill: the money is available again.</summary>
    public void Release(decimal amount)
    {
        AmountApplied = Math.Max(0, AmountApplied - amount);
        if (Status != MpesaStatus.NotFromPos)
            Status = AmountApplied == 0 ? MpesaStatus.Unapplied : AmountApplied >= Amount ? MpesaStatus.Applied : MpesaStatus.PartlyApplied;
    }

    /// <summary>Starts a placeholder code (an STK payment whose code hasn't arrived yet); real codes are letters and digits.</summary>
    public const char PlaceholderMark = '~';

    public bool CodeKnown => !TransactionId.StartsWith(PlaceholderMark);

    public static string? CleanCode(string? code)
    {
        var clean = code?.Trim().ToUpperInvariant();
        return clean is { Length: CodeLength } && clean.All(char.IsAsciiLetterOrDigit) ? clean : null;
    }
}

public enum MpesaPromptStatus
{
    Waiting,
    Paid,
    Failed,
    Cancelled,
    Expired,
}

/// <summary>An STK prompt sent to a customer's phone for a ticket. One may wait per ticket.</summary>
public class MpesaPrompt : Entity
{
    public const int MaxMessageLength = 200;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public Guid TicketId { get; set; }

    /// <summary>Masked; the full number is only in the request to Daraja.</summary>
    public string Phone { get; set; } = "";

    public decimal Amount { get; set; }
    public string CheckoutRequestId { get; set; } = "";
    public string? MerchantRequestId { get; set; }
    public Guid SentByUserId { get; set; }
    public Guid TerminalId { get; set; }

    /// <summary>Sent by a waiter from the order screen (not the cashier's settle screen).</summary>
    public bool FromOrderScreen { get; set; }

    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public MpesaPromptStatus Status { get; set; } = MpesaPromptStatus.Waiting;
    public int? ResultCode { get; set; }

    /// <summary>In plain words ("The customer cancelled the prompt.").</summary>
    public string? ResultMessage { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>When Daraja was last asked about it (the query route, every 5 seconds while waiting).</summary>
    public DateTimeOffset? LastQueriedAt { get; set; }
    public Guid? TransactionId { get; set; }

    /// <summary>The pending payment it added.</summary>
    public Guid? PaymentId { get; set; }

    public bool IsWaiting(DateTimeOffset now) => Status == MpesaPromptStatus.Waiting && now < ExpiresAt;
}

/// <summary>Error codes the screens react to.</summary>
public static class MpesaCodes
{
    public const string UsedUp = "mpesa_used_up";
    public const string PromptWaiting = "mpesa_prompt_waiting";
    public const string NotSetUp = "mpesa_not_set_up";
    public const string PhoneInvalid = "mpesa_phone_invalid";
    public const string Unavailable = "mpesa_unavailable";
}

/// <summary>
/// The business's M-Pesa secrets, encrypted (never returned by any API): the Daraja consumer key and
/// secret, the STK passkey and the relay API key; and when the C2B addresses were last registered.
/// One row.
/// </summary>
public class MpesaAccount : VersionedEntity
{
    public string? ConsumerKey { get; set; }
    public string? ConsumerSecret { get; set; }
    public string? Passkey { get; set; }
    public string? RelayApiKey { get; set; }
    public DateTimeOffset? C2BRegisteredAt { get; set; }

    /// <summary>This business's STK callback address on the relay (it holds the path token), from Test connection.</summary>
    public string? StkCallbackUrl { get; set; }
    public string? C2BRegisteredFor { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
