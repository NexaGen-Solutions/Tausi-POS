using Nexagen.Application.Abstractions;
using Nexagen.Application.Auditing;
using Nexagen.Application.Security;
using Nexagen.Domain.People;

namespace Nexagen.Application.Auth;

/// <summary>The user taps Log out: the session ends on the server, and that is audited.</summary>
public sealed class Logout(INexagenDb db, ICurrentSession session, SessionKeeper sessions, IAuditLog audit)
{
    public async Task Execute(CancellationToken ct)
    {
        var user = session.RequireUser();
        if (user.SessionId is { } id) await sessions.End(id, SessionEndReasons.Logout, ct);
        audit.Write(new AuditRecord(AuditActions.Logout, "User", user.UserId.ToString()));
        await db.SaveChangesAsync(ct);
    }
}
