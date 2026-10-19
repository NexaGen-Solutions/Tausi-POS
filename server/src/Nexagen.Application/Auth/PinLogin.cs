using Nexagen.Application.Auditing;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Security;

namespace Nexagen.Application.Auth;

public sealed record PinLoginRequest(string Pin);

public sealed record LoginResult(SessionUser User, bool MustChangePin);

/// <summary>A user logs in on a terminal with their PIN. The terminal comes from the device token.</summary>
public sealed class PinLogin(INexagenDb db, ICurrentSession session, PinAuthenticator pins, IAuditLog audit)
{
    public async Task<LoginResult> Execute(PinLoginRequest request, CancellationToken ct)
    {
        var terminal = session.RequireTerminal();
        var user = await pins.Authenticate(terminal.TerminalId, request.Pin ?? "", ct);

        audit.Write(new AuditRecord(
            AuditActions.PinLogin, "User", user.Id.ToString(), UserId: user.Id, TerminalId: terminal.TerminalId));
        await db.SaveChangesAsync(ct);

        return new LoginResult(
            new SessionUser(user.Id, user.Name, user.RoleId, user.Role!.Name, terminal.TerminalId, terminal.Name),
            user.MustChangePin);
    }
}
