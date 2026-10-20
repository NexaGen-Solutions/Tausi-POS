namespace Tausi.Application.Common;

/// <summary>The thing asked for does not exist. Becomes HTTP 404.</summary>
public class NotFoundException(string message) : Exception(message);

/// <summary>The request is not allowed in the current state. Becomes HTTP 409.</summary>
public class ConflictException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}

/// <summary>
/// The caller has the permission, but this target or value is off limits to them (e.g. a
/// supervisor editing an admin). Becomes HTTP 403 with a code.
/// </summary>
public class ForbiddenException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>No valid session or device token. Becomes HTTP 401.</summary>
public class UnauthenticatedException(string message) : Exception(message);
