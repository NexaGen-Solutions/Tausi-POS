using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Security;
using Tausi.Application.Settings;

namespace Tausi.Application.Auth;

public sealed record PinLoginRequest(string Pin);

public sealed record LoginResult(
    SessionUser User,
    bool MustChangePin,
    DateTimeOffset ExpiresAt,
    int? IdleLogoutMinutes);

/// <summary>A user logs in on a terminal with their PIN. The terminal comes from the device token.</summary>
public sealed class PinLogin(
    ITausiDb db, ICurrentSession session, PinAuthenticator pins, SessionKeeper sessions, IAuditLog audit)
{
    public async Task<LoginResult> Execute(PinLoginRequest request, CancellationToken ct)
    {
        var terminal = session.RequireTerminal();
        var user = await pins.Authenticate(terminal.TerminalId, request.Pin ?? "", ct);

        var started = await sessions.Start(user.Id, terminal.TerminalId, ct);
        audit.Write(new AuditRecord(
            AuditActions.PinLogin, "User", user.Id.ToString(), UserId: user.Id, TerminalId: terminal.TerminalId));
        await db.SaveChangesAsync(ct);

        var settings = await PosSettings.Load(db, ct);
        return new LoginResult(
            new SessionUser(user.Id, user.Name, user.RoleId, user.Role!.Name, terminal.TerminalId, terminal.Name, started.Id),
            user.MustChangePin,
            started.ExpiresAt,
            settings.IdleLogoutMinutesIfOn);
    }
}
