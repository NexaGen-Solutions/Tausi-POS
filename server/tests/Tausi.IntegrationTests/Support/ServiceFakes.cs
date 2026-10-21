using Tausi.Application.Events;
using Tausi.Application.Printing;
using Tausi.Application.Security;

namespace Tausi.IntegrationTests.Support;

/// <summary>Stand-ins for running use cases straight against a database, without the web host.</summary>
public sealed class FixedSession : ICurrentSession
{
    public SessionUser? User { get; set; }
    public DeviceTerminal? Terminal { get; set; }
}

public sealed class NoApproval : IApprovalTokenSource
{
    public string? ApprovalToken => null;
}

public sealed class NoEvents : IPosEvents
{
    public Task Publish(string eventName, object payload, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class NoLogo : ILogoProvider
{
    public Task<MonoBitmap?> GetLogo(int maxWidthDots, CancellationToken ct = default) => Task.FromResult<MonoBitmap?>(null);
}

/// <summary>Plain hashes: the seed needs PINs, these tests never log in.</summary>
public sealed class PlainPinHasher : IPinHasher
{
    public string Hash(string secret) => "plain:" + secret;
    public bool Verify(string secret, string hash) => hash == "plain:" + secret;
    public string Lookup(string pin) => "lookup:" + pin;
}
