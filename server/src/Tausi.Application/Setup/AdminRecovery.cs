using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Application.Staff;
using Tausi.Domain.Common;
using Tausi.Domain.Setup;

namespace Tausi.Application.Setup;

public sealed record RecoverAdminRequest(string Token, string? UserName, string Password);

/// <summary>
/// A lost admin-site password. <c>TausiServer reset-admin</c>, run on the server PC, prints a new
/// setup token there (and puts it in setup-token.txt); it lasts an hour. With it, /setup/recover sets a new
/// password for an admin user. Proof of being at the server is the point: the token never leaves it
/// except by being read off its screen. Wrong tokens lock it like the setup token.
/// </summary>
public sealed class AdminRecovery(ITausiDb db, ISetupTokenFile tokenFile, IPinHasher hasher, IAuditLog audit, TimeProvider clock)
{
    public const string Actor = "recovery";
    public static readonly TimeSpan Lasts = TimeSpan.FromHours(1);

    public async Task<string> Issue(CancellationToken ct)
    {
        var state = await db.SetupStates.OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (state?.IsComplete != true) throw new ConflictException("Setup isn't finished: open /setup with the setup token instead.", SetupCodes.Required);
        var token = SetupFlow.NewToken();
        state.TokenHash = SecureTokens.Hash(token);
        state.TokenCreatedAt = clock.GetUtcNow();
        state.FailedAttempts = 0;
        state.LockedUntil = null;
        await db.SaveChangesAsync(ct);
        tokenFile.Write(token);
        return token;
    }

    public async Task<string> Recover(RecoverAdminRequest r, CancellationToken ct)
    {
        var state = await db.SetupStates.OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        var now = clock.GetUtcNow();
        if (state?.IsComplete != true || state.TokenHash is null || state.TokenCreatedAt is null || now - state.TokenCreatedAt > Lasts)
            throw new DomainException("There is no reset waiting. Run `TausiServer reset-admin` on the server first.", "recovery_none");
        if (state.LockedUntil is { } until && until > now) throw new LoginLockedException(until);
        if (SecureTokens.Hash(SetupFlow.Canonical(r.Token)) != state.TokenHash)
        {
            state.FailedAttempts++;
            if (state.FailedAttempts >= SetupState.MaxTries)
            {
                state.FailedAttempts = 0;
                state.LockedUntil = now + SetupState.LockTime;
                await db.SaveChangesAsync(ct);
                throw new LoginLockedException(state.LockedUntil.Value);
            }
            await db.SaveChangesAsync(ct);
            throw new DomainException($"That isn't the token `reset-admin` printed. {SetupState.MaxTries - state.FailedAttempts} tries left.", SetupCodes.WrongToken);
        }
        if ((r.Password ?? "").Length < StaffAccess.MinPasswordLength)
            throw new FieldErrorsException(new Dictionary<string, string> { ["password"] = $"At least {StaffAccess.MinPasswordLength} characters." });

        var name = (r.UserName ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.Include(u => u.Role)
            .Where(u => u.Active && u.UserName != null && u.Role!.CanUseAdminSite && (name == "" || u.UserName == name))
            .OrderBy(u => u.UserName).FirstOrDefaultAsync(ct)
            ?? throw new DomainException("No active admin-site user by that name.", "recovery_user");
        user.PasswordHash = hasher.Hash(r.Password!);
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        state.TokenHash = null;
        state.TokenCreatedAt = null;
        audit.Write(new AuditRecord(AuditActions.AdminPasswordReset, "User", user.Id.ToString(), NewValue: new { user.UserName }, UserId: user.Id, Actor: Actor));
        await db.SaveChangesAsync(ct);
        tokenFile.Delete();
        return user.UserName!;
    }
}
