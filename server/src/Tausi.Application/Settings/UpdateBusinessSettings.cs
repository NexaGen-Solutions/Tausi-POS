using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Settings;

namespace Tausi.Application.Settings;

/// <summary>
/// <c>RowVersion</c> is the version the editor loaded; a newer saved version wins and this save is refused.
/// <c>TimeZone</c> is an IANA name such as "Africa/Nairobi"; null keeps the current one.
/// </summary>
public sealed record UpdateBusinessSettingsRequest(
    string Name,
    string Address,
    string Phone,
    decimal VatRate,
    string Currency,
    RoundingRule Rounding,
    string ReceiptFooter,
    PrintMode PrintMode,
    string? OtherJson,
    long RowVersion,
    string? TimeZone = null);

public sealed class UpdateBusinessSettings(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    IAuditLog audit, IPosEvents events)
{
    public async Task<BusinessSettingsDto> Execute(UpdateBusinessSettingsRequest request, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);
        Validate(request);

        var settings = await db.BusinessSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("The business has not been set up yet.");
        if (settings.RowVersion != request.RowVersion)
            throw new ConflictException("Someone else changed the settings. Reload and try again.");

        var before = BusinessSettingsDto.From(settings);
        settings.Name = request.Name.Trim();
        settings.Address = request.Address?.Trim() ?? "";
        settings.Phone = request.Phone?.Trim() ?? "";
        settings.VatRate = request.VatRate;
        settings.Currency = request.Currency.Trim().ToUpperInvariant();
        settings.Rounding = request.Rounding;
        settings.ReceiptFooter = request.ReceiptFooter?.Trim() ?? "";
        settings.PrintMode = request.PrintMode;
        settings.OtherJson = string.IsNullOrWhiteSpace(request.OtherJson) ? "{}" : request.OtherJson;
        if (request.TimeZone is not null) settings.TimeZone = request.TimeZone.Trim();

        audit.Write(new AuditRecord(
            AuditActions.SettingsChanged, nameof(BusinessSettings), settings.Id.ToString(),
            OldValue: before, NewValue: BusinessSettingsDto.From(settings) with { RowVersion = before.RowVersion + 1 },
            ApprovedByUserId: grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await events.Publish(PosEventNames.SettingsChanged, new { }, ct);
        return BusinessSettingsDto.From(settings);
    }

    private static void Validate(UpdateBusinessSettingsRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new DomainException("The business name is required.");
        if (r.VatRate is < 0m or >= 100m) throw new DomainException("The VAT rate must be from 0 to below 100.");
        if (string.IsNullOrWhiteSpace(r.Currency) || r.Currency.Trim().Length != 3)
            throw new DomainException("The currency must be a 3-letter code, e.g. KES.");
        if (r.TimeZone is not null && !BusinessTimeZone.IsValid(r.TimeZone))
            throw new DomainException("The time zone must be a name such as Africa/Nairobi.");
        if (!string.IsNullOrWhiteSpace(r.OtherJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(r.OtherJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                PosSettings.Validate(doc.RootElement);
                PrintSettings.Validate(doc.RootElement);
            }
            catch (JsonException)
            {
                throw new DomainException("Other settings must be a JSON object.");
            }
        }
    }
}
