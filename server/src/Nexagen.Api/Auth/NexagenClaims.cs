namespace Nexagen.Api.Auth;

public static class NexagenClaims
{
    public const string UserId = "sub";
    public const string Name = "name";
    public const string TerminalId = "tid";

    public const string PinScheme = "Pin";
    public const string AdminScheme = "Admin";
    public const string SessionScheme = "Session";

    public const string DeviceTokenHeader = "X-Device-Token";
    public const string DeviceTokenQuery = "device_token";
    public const string AppVersionHeader = "X-App-Version";
    public const string ApprovalTokenHeader = "X-Approval-Token";
    public const string IdempotencyKeyHeader = "Idempotency-Key";
}
