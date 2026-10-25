using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Backups;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.People;

namespace Tausi.Application.Terminals;

public sealed record MoveTerminalsRequest(string ServerUrl);

/// <summary>Asks another Tausi POS server which business it holds (null when it doesn't answer).</summary>
public interface IServerProbe
{
    Task<string?> BusinessName(string serverUrl, CancellationToken ct);
}

/// <summary>
/// Moving to a new server PC, after its backup was restored there: one action on the old server's admin
/// site sends every running terminal to the new address. They keep their pairing (the restored database
/// knows their device tokens). The new server must answer, and hold the same business. Needs <c>devices.manage</c>.
/// </summary>
public sealed class MoveTerminals(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval, IServerProbe probe,
    IAuditLog audit, IPosEvents events)
{
    public static string? Origin(string? address)
    {
        if (!Uri.TryCreate((address ?? "").Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
            return null;
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    public async Task<string> Execute(MoveTerminalsRequest request, CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.DevicesManage, approval.ApprovalToken, ct);
        var origin = Origin(request.ServerUrl)
            ?? throw new Common.FieldErrorsException(new Dictionary<string, string> { ["serverUrl"] = "Enter an address like http://192.168.1.20:5080" });
        var business = (await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessName);
        var there = await probe.BusinessName(origin, ct)
            ?? throw new DomainException($"Nothing answers at {origin}. Install the server there and restore the backup first.", "move_unreachable");
        if (!BackupAdmin.SameName(there, business))
            throw new DomainException($"The server at {origin} holds '{there}', not '{business}'. Restore this business's backup there first.", "move_other_business");

        audit.Write(new AuditRecord(AuditActions.TerminalsMoved, "Server", origin, NewValue: new { serverUrl = origin }));
        await db.SaveChangesAsync(ct);
        await events.Publish(PosEventNames.TerminalsMove, new { serverUrl = origin }, ct);
        return origin;
    }
}
