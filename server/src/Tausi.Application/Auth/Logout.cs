using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Security;
using Tausi.Domain.People;

namespace Tausi.Application.Auth;

/// <summary>The user taps Log out: the session ends on the server, and that is audited.</summary>
public sealed class Logout(ITausiDb db, ICurrentSession session, SessionKeeper sessions, IAuditLog audit)
{
    public async Task Execute(CancellationToken ct)
    {
        var user = session.RequireUser();
        if (user.SessionId is { } id) await sessions.End(id, SessionEndReasons.Logout, ct);
        audit.Write(new AuditRecord(AuditActions.Logout, "User", user.UserId.ToString()));
        await db.SaveChangesAsync(ct);
    }
}
