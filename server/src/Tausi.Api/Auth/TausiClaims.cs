namespace Tausi.Api.Auth;

public static class TausiClaims
{
    public const string UserId = "sub";
    public const string Name = "name";
    public const string TerminalId = "tid";
    public const string SessionId = "sid";

    /// <summary>Problem "code" for a PIN session that has ended (idle, logged out, or too old).</summary>
    public const string SessionExpiredCode = "session_expired";

    /// <summary>Problem "code" when the user must choose a new PIN before doing anything else.</summary>
    public const string PinChangeRequiredCode = "pin_change_required";

    /// <summary>Hub policy: a paired terminal (even before anyone logs in) or a signed-in user.</summary>
    public const string DeviceOrSessionPolicy = "DeviceOrSession";

    public const string PinScheme = "Pin";
    public const string AdminScheme = "Admin";
    public const string SessionScheme = "Session";

    public const string DeviceTokenHeader = "X-Device-Token";
    public const string DeviceTokenQuery = "device_token";
    public const string AppVersionHeader = "X-App-Version";
    public const string ApprovalTokenHeader = "X-Approval-Token";
    public const string IdempotencyKeyHeader = "Idempotency-Key";
}
