using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Security;
using Tausi.Domain.People;

namespace Tausi.Application.Auth;

public sealed record IssueApprovalRequest(string Pin, string PermissionKey);

public sealed record ApprovalIssued(string ApprovalToken, Guid ApprovedByUserId, string ApprovedByName, DateTimeOffset ExpiresAt);

/// <summary>
/// A supervisor types their PIN on the approval pad. If their role has the permission Allowed, they
/// get a single-use token for that permission, valid for 60 seconds. Wrong PINs count toward the
/// terminal's lockout, like a login.
/// </summary>
public sealed class IssueApproval(
    ITausiDb db, ICurrentSession session, PinAuthenticator pins, IPermissionService permissions,
    IAuditLog audit, TimeProvider clock)
{
    public async Task<ApprovalIssued> Execute(IssueApprovalRequest request, CancellationToken ct)
    {
        var terminal = session.RequireTerminal();
        if (!PermissionKeys.IsKnown(request.PermissionKey ?? ""))
            throw new Domain.Common.DomainException("Unknown permission.");

        var approver = await pins.Authenticate(terminal.TerminalId, request.Pin ?? "", ct);
        var approverSession = new SessionUser(
            approver.Id, approver.Name, approver.RoleId, approver.Role!.Name, terminal.TerminalId, terminal.Name);
        if (await permissions.Check(approverSession, request.PermissionKey!, ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(request.PermissionKey!);

        var now = clock.GetUtcNow();
        var token = SecureTokens.New();
        var grant = new ApprovalGrant
        {
            TokenHash = SecureTokens.Hash(token),
            PermissionKey = request.PermissionKey!,
            ApprovedByUserId = approver.Id,
            TerminalId = terminal.TerminalId,
            CreatedAt = now,
            ExpiresAt = now + ApprovalGrant.Lifetime,
        };
        db.ApprovalGrants.Add(grant);
        audit.Write(new AuditRecord(
            AuditActions.ApprovalIssued, "ApprovalGrant", grant.Id.ToString(),
            NewValue: new { permission = grant.PermissionKey, requestedBy = session.User?.UserId },
            UserId: approver.Id, TerminalId: terminal.TerminalId));
        await db.SaveChangesAsync(ct);

        return new ApprovalIssued(token, approver.Id, approver.Name, grant.ExpiresAt);
    }
}
