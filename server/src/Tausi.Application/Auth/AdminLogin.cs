using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Security;

namespace Tausi.Application.Auth;

public sealed record AdminLoginRequest(string UserName, string Password);

/// <summary>
/// Admin-site sign-in with a user name and password. Only roles marked <c>CanUseAdminSite</c>
/// may sign in. 5 wrong passwords in a row lock that user for 5 minutes.
/// </summary>
public sealed class AdminLogin(ITausiDb db, IPinHasher hasher, IAuditLog audit, TimeProvider clock)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(5);

    public async Task<SessionUser> Execute(AdminLoginRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var name = (request.UserName ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.UserName == name && u.Active, ct);

        if (user is null || user.PasswordHash is null || !user.Role!.CanUseAdminSite)
        {
            // Same answer as a wrong password, so names cannot be probed.
            audit.Write(new AuditRecord(AuditActions.AdminLoginFailed, NewValue: new { userName = name }));
            await db.SaveChangesAsync(ct);
            throw new LoginRejectedException("Wrong user name or password.");
        }
        if (user.LockedUntil > now)
            throw new LoginLockedException(user.LockedUntil.Value);

        if (!hasher.Verify(request.Password ?? "", user.PasswordHash))
        {
            user.FailedAttempts++;
            if (user.FailedAttempts >= MaxFailures)
            {
                user.LockedUntil = now + LockFor;
                user.FailedAttempts = 0;
            }
            audit.Write(new AuditRecord(AuditActions.AdminLoginFailed, "User", user.Id.ToString(), UserId: user.Id));
            await db.SaveChangesAsync(ct);
            if (user.LockedUntil > now) throw new LoginLockedException(user.LockedUntil.Value);
            throw new LoginRejectedException("Wrong user name or password.");
        }

        user.FailedAttempts = 0;
        user.LockedUntil = null;
        audit.Write(new AuditRecord(AuditActions.AdminLogin, "User", user.Id.ToString(), UserId: user.Id));
        await db.SaveChangesAsync(ct);
        return new SessionUser(user.Id, user.Name, user.RoleId, user.Role.Name, null, null);
    }
}
