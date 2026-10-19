using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Security;
using Nexagen.Infrastructure.Persistence;

namespace Nexagen.Api.Auth;

/// <summary>
/// The caller, from the authenticated session and the device token. The user and role are read
/// fresh from the database, so deactivating someone or changing their role applies at once.
/// </summary>
public sealed class HttpCurrentSession(IHttpContextAccessor accessor, NexagenDbContext db) : ICurrentSession
{
    private bool _loaded;
    private SessionUser? _user;

    public DeviceTerminal? Terminal => accessor.HttpContext?.Features.Get<DeviceTerminal>();

    public SessionUser? User
    {
        get
        {
            if (_loaded) return _user;
            _loaded = true;
            var principal = accessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true) return null;
            if (!Guid.TryParse(principal.FindFirst(NexagenClaims.UserId)?.Value, out var userId)) return null;

            var user = db.Users.AsNoTracking()
                .Where(u => u.Id == userId && u.Active)
                .Select(u => new { u.Id, u.Name, u.RoleId, RoleName = u.Role!.Name })
                .FirstOrDefault();
            if (user is null) return null;

            // PIN sessions belong to a terminal (checked against the device token at sign-in);
            // admin-site sessions have none.
            var terminal = Terminal;
            var onTerminal = principal.FindFirst(NexagenClaims.TerminalId) is not null;
            _user = new SessionUser(
                user.Id, user.Name, user.RoleId, user.RoleName,
                onTerminal ? terminal?.TerminalId : null,
                onTerminal ? terminal?.Name : null);
            return _user;
        }
    }
}

public sealed class HeaderApprovalTokenSource(IHttpContextAccessor accessor) : IApprovalTokenSource
{
    public string? ApprovalToken => accessor.HttpContext?.Request.Headers[NexagenClaims.ApprovalTokenHeader].FirstOrDefault();
}
