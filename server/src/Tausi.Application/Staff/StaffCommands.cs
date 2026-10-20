using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.People;

namespace Tausi.Application.Staff;

/// <summary>Adds a person. They always get MustChangePin; the starting PIN is shown once.</summary>
public sealed class CreateStaff(
    ITausiDb db, StaffAccess access, PinRules pins, IPinHasher hasher, IAuditLog audit, TimeProvider clock)
{
    public async Task<StaffSaved> Execute(CreateStaffRequest request, CancellationToken ct)
    {
        var actor = await access.RequireActor(ct);
        var role = await access.RequireAssignableRole(actor, request.RoleId, ct);
        var name = await access.ValidName(request.Name, null, ct);
        var pin = request.StartingPin ?? "";
        await pins.Check(pin, null, ct);

        var user = new User { Name = name, RoleId = role.Id, Role = role, MustChangePin = true };
        pins.Apply(user, pin);
        if (role.CanUseAdminSite)
        {
            if (string.IsNullOrEmpty(request.AdminPassword))
                throw new DomainException("Set an admin-site password for this role.", StaffCodes.PasswordRequired);
            StaffAccess.CheckPassword(request.AdminPassword);
            user.PasswordHash = hasher.Hash(request.AdminPassword);
            user.UserName = await access.NewUserName(name, ct);
        }

        db.Users.Add(user);
        audit.Write(StaffAccess.Audit(AuditActions.StaffCreated, user, null, StaffAccess.AuditView(user, role.Name), actor));
        await StaffSave.Save(db, ct);
        return new StaffSaved((await StaffQueries.Rows(db, [user], clock.GetUtcNow(), ct))[0], pin);
    }
}

/// <summary>Changes name, role or admin-site password. Not the PIN (that is Reset PIN).</summary>
public sealed class UpdateStaff(
    ITausiDb db, StaffAccess access, IPinHasher hasher, IAuditLog audit, TimeProvider clock)
{
    public async Task<StaffMemberDto> Execute(Guid id, UpdateStaffRequest request, CancellationToken ct)
    {
        var actor = await access.RequireActor(ct);
        var user = await access.LoadManageable(actor, id, request.RowVersion, ct);
        var before = StaffAccess.AuditView(user, user.Role!.Name);

        if (request.RoleId != user.RoleId)
        {
            if (user.Id == actor.User.UserId)
                throw new ForbiddenException("You can't change your own role. Ask another admin.", StaffCodes.OwnRole);
            var role = await access.RequireAssignableRole(actor, request.RoleId, ct);
            var tiers = await access.RoleTiers(ct);
            if (tiers.GetValueOrDefault(role.Id) != StaffTier.Admin) await access.EnsureNotLastAdmin(user, ct);
            user.RoleId = role.Id;
            user.Role = role;
        }

        user.Name = await access.ValidName(request.Name, user.Id, ct);
        var passwordChanged = false;
        if (!string.IsNullOrEmpty(request.AdminPassword))
        {
            if (!user.Role!.CanUseAdminSite)
                throw new DomainException("This role does not use the admin site.");
            StaffAccess.CheckPassword(request.AdminPassword);
            user.PasswordHash = hasher.Hash(request.AdminPassword);
            user.UserName ??= await access.NewUserName(user.Name, ct);
            user.FailedAttempts = 0;
            user.LockedUntil = null;
            passwordChanged = true;
        }

        var after = StaffAccess.AuditView(user, user.Role!.Name);
        audit.Write(StaffAccess.Audit(AuditActions.StaffUpdated, user, before, new { after, passwordChanged }, actor));
        await StaffSave.Save(db, ct);
        return (await StaffQueries.Rows(db, [user], clock.GetUtcNow(), ct))[0];
    }
}

/// <summary>Sets a temporary PIN (typed or generated): MustChangePin, and every session ends now.</summary>
public sealed class ResetStaffPin(
    ITausiDb db, StaffAccess access, PinRules pins, SessionKeeper sessions, IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public async Task<StaffSaved> Execute(Guid id, SetPinRequest request, CancellationToken ct)
    {
        var actor = await access.RequireActor(ct);
        var user = await access.LoadManageable(actor, id, request.RowVersion, ct);
        var pin = request.Generate ? await pins.Generate(user.Id, ct) : request.NewPin ?? "";
        await pins.Check(pin, user.Id, ct);

        pins.Apply(user, pin);
        user.MustChangePin = true;
        audit.Write(StaffAccess.Audit(AuditActions.StaffPinReset, user, null, new { mustChangePin = true }, actor));
        await StaffSave.Save(db, ct);
        await sessions.EndAllFor(user.Id, SessionEndReasons.PinReset, ct);
        await events.Publish(PosEventNames.UserPinReset, new { userId = user.Id }, ct);
        return new StaffSaved((await StaffQueries.Rows(db, [user], clock.GetUtcNow(), ct))[0], pin);
    }
}

/// <summary>
/// Stops someone logging in and ends their sessions on every terminal at once. Their history
/// stays linked to them, and their name stays taken.
/// </summary>
public sealed class DeactivateStaff(
    ITausiDb db, StaffAccess access, SessionKeeper sessions, IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public async Task<StaffMemberDto> Execute(Guid id, RowVersionRequest request, CancellationToken ct)
    {
        var actor = await access.RequireActor(ct);
        if (id == actor.User.UserId)
        {
            // The last admin hears why first; anyone else is told to ask another admin.
            var self = await db.Users.AsNoTracking().FirstAsync(u => u.Id == id, ct);
            await access.EnsureNotLastAdmin(self, ct);
            throw new ForbiddenException("You can't deactivate yourself. Ask another admin.", StaffCodes.SelfDeactivate);
        }
        var user = await access.LoadManageable(actor, id, request.RowVersion, ct);
        if (!user.Active) return (await StaffQueries.Rows(db, [user], clock.GetUtcNow(), ct))[0];
        await access.EnsureNotLastAdmin(user, ct);

        var before = StaffAccess.AuditView(user, user.Role!.Name);
        user.Active = false;
        audit.Write(StaffAccess.Audit(AuditActions.StaffDeactivated, user, before, StaffAccess.AuditView(user, user.Role.Name), actor));
        await StaffSave.Save(db, ct);
        await sessions.EndAllFor(user.Id, SessionEndReasons.Deactivated, ct);
        await events.Publish(PosEventNames.UserDeactivated, new { userId = user.Id }, ct);
        return (await StaffQueries.Rows(db, [user], clock.GetUtcNow(), ct))[0];
    }
}

/// <summary>Lets someone log in again, with a new temporary PIN they must change.</summary>
public sealed class ReactivateStaff(
    ITausiDb db, StaffAccess access, PinRules pins, IAuditLog audit, TimeProvider clock)
{
    public async Task<StaffSaved> Execute(Guid id, SetPinRequest request, CancellationToken ct)
    {
        var actor = await access.RequireActor(ct);
        var user = await access.LoadManageable(actor, id, request.RowVersion, ct);
        if (user.Active) throw new ConflictException($"{user.Name} is already active.");
        var pin = request.Generate ? await pins.Generate(user.Id, ct) : request.NewPin ?? "";
        await pins.Check(pin, user.Id, ct);

        var before = StaffAccess.AuditView(user, user.Role!.Name);
        pins.Apply(user, pin);
        user.Active = true;
        user.MustChangePin = true;
        audit.Write(StaffAccess.Audit(AuditActions.StaffReactivated, user, before, StaffAccess.AuditView(user, user.Role.Name), actor));
        await StaffSave.Save(db, ct);
        return new StaffSaved((await StaffQueries.Rows(db, [user], clock.GetUtcNow(), ct))[0], pin);
    }
}

internal static class StaffSave
{
    /// <summary>Saves, turning races the database catches into clear answers.</summary>
    public static async Task Save(ITausiDb db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(StaffCodes.StaleMessage, StaffCodes.Stale);
        }
        catch (DbUpdateException)
        {
            // A unique index: the same name or PIN was taken a moment ago.
            throw new DomainException("That name or PIN was just taken. Choose another.", StaffCodes.NameTaken);
        }
    }
}
