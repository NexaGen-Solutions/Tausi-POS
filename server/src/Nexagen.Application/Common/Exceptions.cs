namespace Nexagen.Application.Common;

/// <summary>The thing asked for does not exist. Becomes HTTP 404.</summary>
public class NotFoundException(string message) : Exception(message);

/// <summary>The request is not allowed in the current state. Becomes HTTP 409.</summary>
public class ConflictException(string message) : Exception(message);

/// <summary>No valid session or device token. Becomes HTTP 401.</summary>
public class UnauthenticatedException(string message) : Exception(message);
