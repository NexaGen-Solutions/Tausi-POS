using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Tickets;

/// <param name="WasPoured">Was the drink already poured? "Spilled or broken" is always yes; otherwise No by default.</param>
public sealed record VoidLineRequest(decimal Quantity, string Reason, string? Note = null, bool? WasPoured = null);

public sealed record GiftLineRequest(decimal Quantity, string Reason, string? Note = null);

/// <summary>Kind null removes the ticket's discount.</summary>
public sealed record DiscountRequest(DiscountKind? Kind, decimal Value, string? Reason, string? Note = null);

public sealed record ChangePriceRequest(decimal Quantity, decimal NewUnitPrice, string Reason);

public sealed record ReprintRequest(string? Reason = null);

/// <summary>
/// Who does a ticket action: the requester (their terminal) and who approved it. When the
/// requester is Allowed, they approve it themselves. <see cref="RequestId"/> is set when it runs
/// from an approved request.
/// </summary>
public sealed record TicketActor(Guid UserId, string UserName, Guid TerminalId, Guid ApprovedById, string ApproverName, Guid? RequestId = null);

/// <summary>What an action would do, for the request list: "Void 1 × Guiness", KSh 300, "Wrong item".</summary>
public sealed record ActionSummary(string Text, decimal? Amount, string? Reason, Guid? LineId);

public static class ActionReasons
{
    public const string Other = "Other";
    public const int MaxNoteLength = ApprovalRequest.MaxNoteLength;

    public static readonly IReadOnlyList<string> Void = ["Wrong item", "Customer changed mind", "Spilled or broken", Other];
    public static readonly IReadOnlyList<string> Gift = ["Regular customer", "House", "Compensation", Other];
    public static readonly IReadOnlyList<string> Discount = ["Regular customer", "Promotion", "Compensation", Other];

    public const string SpilledOrBroken = "Spilled or broken";

    /// <summary>
    /// Checks the reason is one of the list and that "Other" has a note. Returns the text shown and
    /// printed: the reason, or "Other: the note".
    /// </summary>
    public static string Text(IReadOnlyList<string> allowed, string? reason, string? note)
    {
        var clean = reason?.Trim() ?? "";
        var match = allowed.FirstOrDefault(r => string.Equals(r, clean, StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainException("Choose a reason.", "reason_required");
        var cleanNote = Note(note);
        if (match == Other)
            return cleanNote is null ? throw new DomainException("Add a note for \"Other\".", "note_required") : $"Other: {cleanNote}";
        return match;
    }

    public static string? Note(string? note)
    {
        var clean = note?.Trim();
        if (clean is { Length: > MaxNoteLength }) throw new DomainException($"A note can be at most {MaxNoteLength} characters.");
        return string.IsNullOrEmpty(clean) ? null : clean;
    }

    /// <summary>A free-text reason (price change, reprint).</summary>
    public static string FreeText(string? reason, bool required)
    {
        var clean = Note(reason);
        if (required && clean is null) throw new DomainException("Give a reason.", "reason_required");
        return clean ?? "";
    }
}

public static class ApprovalKinds
{
    public static string Permission(ApprovalKind kind) => kind switch
    {
        ApprovalKind.Void => PermissionKeys.OrderVoid,
        ApprovalKind.Gift => PermissionKeys.OrderGift,
        ApprovalKind.Discount => PermissionKeys.OrderDiscount,
        ApprovalKind.PriceChange => PermissionKeys.OrderChangePrice,
        ApprovalKind.Reprint => PermissionKeys.TicketReprint,
        ApprovalKind.Move => PermissionKeys.TicketMove,
        ApprovalKind.Merge => PermissionKeys.TicketMerge,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>"void", "gift", "price change" … for "MAU approved the void".</summary>
    public static string Noun(ApprovalKind kind) => kind switch
    {
        ApprovalKind.PriceChange => "price change",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
