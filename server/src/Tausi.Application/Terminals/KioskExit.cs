using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Auth;
using Tausi.Application.Security;

namespace Tausi.Application.Terminals;

/// <summary>
/// Closing the terminal app in kiosk mode (after the hidden gesture): needs an admin-site login, so a
/// technician can leave the POS but a cashier can't. Recorded against the terminal.
/// </summary>
public sealed class KioskExit(ITausiDb db, ICurrentSession session, AdminLogin login, IAuditLog audit)
{
    public async Task Execute(AdminLoginRequest request, string? ip, CancellationToken ct)
    {
        var terminal = session.RequireTerminal();
        var user = await login.Execute(request, ct, ip);
        audit.Write(new AuditRecord(AuditActions.TerminalKioskExit, "Terminal", terminal.TerminalId.ToString(),
            NewValue: new { terminal.Name }, UserId: user.UserId, TerminalId: terminal.TerminalId));
        await db.SaveChangesAsync(ct);
    }
}
