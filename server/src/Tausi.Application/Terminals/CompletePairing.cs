using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Devices;
using Tausi.Domain.People;

namespace Tausi.Application.Terminals;

/// <summary>Give either the terminal's id or its name; a new name creates the terminal.</summary>
public sealed record CompletePairingRequest(string Code, Guid? TerminalId, string? TerminalName);

public sealed record PairingCompleted(Guid TerminalId, string TerminalName);

/// <summary>
/// An admin enters the code shown on a terminal app and picks which terminal it is. The app then
/// collects its device token. Pairing again replaces the old token, so the old app stops working.
/// </summary>
public sealed class CompletePairing(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    IAuditLog audit, TimeProvider clock)
{
    public async Task<PairingCompleted> Execute(CompletePairingRequest request, CancellationToken ct)
    {
        var user = session.RequireUser();
        var grant = await permissions.Require(user, PermissionKeys.DevicesManage, approval.ApprovalToken, ct);

        var now = clock.GetUtcNow();
        var code = (request.Code ?? "").Replace("-", "").Replace(" ", "");
        var pairing = await db.TerminalPairings
            .FirstOrDefaultAsync(p => p.Code == code && p.ExpiresAt > now && p.TerminalId == null, ct)
            ?? throw new NotFoundException("That pairing code is not valid or has expired.");

        Terminal? terminal;
        if (request.TerminalId is { } id)
        {
            terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new NotFoundException("Terminal not found.");
        }
        else
        {
            var name = request.TerminalName?.Trim();
            if (string.IsNullOrEmpty(name)) throw new DomainException("Give the terminal's name.");
            terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Name == name, ct);
            if (terminal is null)
            {
                terminal = new Terminal { Name = name };
                db.Terminals.Add(terminal);
            }
        }

        var deviceToken = SecureTokens.New();
        var wasPaired = terminal.DeviceTokenHash is not null;
        terminal.DeviceTokenHash = SecureTokens.Hash(deviceToken);
        pairing.TerminalId = terminal.Id;
        pairing.PendingDeviceToken = deviceToken;

        audit.Write(new AuditRecord(
            AuditActions.TerminalPaired, "Terminal", terminal.Id.ToString(),
            OldValue: new { paired = wasPaired }, NewValue: new { paired = true, terminal.Name },
            ApprovedByUserId: grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        return new PairingCompleted(terminal.Id, terminal.Name);
    }
}
