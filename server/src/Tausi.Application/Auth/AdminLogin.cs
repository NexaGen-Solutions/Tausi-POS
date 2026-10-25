using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Security;

namespace Tausi.Application.Auth;

public sealed record AdminLoginRequest(string UserName, string Password);

/// <summary>
/// Admin-site sign-in with a user name and password. Only roles marked <c>CanUseAdminSite</c>
/// may sign in. 10 wrong passwords in 10 minutes lock that user name, and the IP address they came
/// from, until 10 minutes after the first of them (<see cref="LoginThrottle"/>).
/// </summary>
public sealed class AdminLogin(ITausiDb db, IPinHasher hasher, IAuditLog audit, LoginThrottle throttle)
{
    public async Task<SessionUser> Execute(AdminLoginRequest request, CancellationToken ct, string? ip = null)
    {
        var name = (request.UserName ?? "").Trim().ToLowerInvariant();
        var keys = new[] { LoginThrottle.UserKey(name), LoginThrottle.IpKey(ip) };
        if (throttle.LockedUntil(keys) is { } locked) throw new LoginLockedException(locked);
        var user = await db.Users.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.UserName == name && u.Active, ct);

        if (user is null || user.PasswordHash is null || !user.Role!.CanUseAdminSite)
        {
            // Same answer as a wrong password, so names cannot be probed.
            audit.Write(new AuditRecord(AuditActions.AdminLoginFailed, NewValue: new { userName = name, ip }));
            await db.SaveChangesAsync(ct);
            throw Failed(keys);
        }

        if (!hasher.Verify(request.Password ?? "", user.PasswordHash))
        {
            audit.Write(new AuditRecord(AuditActions.AdminLoginFailed, "User", user.Id.ToString(), NewValue: new { ip }, UserId: user.Id));
            await db.SaveChangesAsync(ct);
            throw Failed(keys);
        }

        throttle.Clear(keys[0]);
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        audit.Write(new AuditRecord(AuditActions.AdminLogin, "User", user.Id.ToString(), UserId: user.Id));
        await db.SaveChangesAsync(ct);
        return new SessionUser(user.Id, user.Name, user.RoleId, user.Role.Name, null, null);
    }

    private Exception Failed(string[] keys)
    {
        throttle.Fail(keys);
        return throttle.LockedUntil(keys) is { } until
            ? new LoginLockedException(until)
            : new LoginRejectedException("Wrong user name or password.");
    }
}
