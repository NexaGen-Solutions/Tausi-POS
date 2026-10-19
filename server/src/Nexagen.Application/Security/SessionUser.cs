namespace Nexagen.Application.Security;

/// <summary>
/// Who is acting, taken from the authenticated session (never from a request body).
/// <see cref="TerminalId"/> and <see cref="SessionId"/> are set for PIN sessions on a terminal and
/// null on the admin site.
/// </summary>
public sealed record SessionUser(
    Guid UserId,
    string Name,
    Guid RoleId,
    string RoleName,
    Guid? TerminalId,
    string? TerminalName,
    Guid? SessionId = null);

/// <summary>The terminal identified by the device token on this request, if any.</summary>
public sealed record DeviceTerminal(Guid TerminalId, string Name);

/// <summary>The caller of the current request. Filled in by the API from the session and device token.</summary>
public interface ICurrentSession
{
    SessionUser? User { get; }
    DeviceTerminal? Terminal { get; }

    SessionUser RequireUser() =>
        User ?? throw new Common.UnauthenticatedException("Please log in.");

    DeviceTerminal RequireTerminal() =>
        Terminal ?? throw new Common.UnauthenticatedException("This device is not a registered terminal.");
}
