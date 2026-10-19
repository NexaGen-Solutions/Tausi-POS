using System.Security.Cryptography;
using System.Text;
using Nexagen.Application.Events;
using Nexagen.Application.Security;

namespace Nexagen.Tests.Support;

public sealed class FakeSession : ICurrentSession
{
    public SessionUser? User { get; set; }
    public DeviceTerminal? Terminal { get; set; }
}

public sealed class FakeApprovalSource : IApprovalTokenSource
{
    public string? ApprovalToken { get; set; }
}

public sealed class RecordingEvents : IPosEvents
{
    public List<(string Name, object Payload)> Published { get; } = [];

    public Task Publish(string eventName, object payload, CancellationToken ct = default)
    {
        Published.Add((eventName, payload));
        return Task.CompletedTask;
    }
}

/// <summary>Fast stand-in for the PBKDF2 hasher (unit tests do not need slow hashing).</summary>
public sealed class FastPinHasher : IPinHasher
{
    public string Hash(string secret) => "fast$" + Lookup(secret);
    public bool Verify(string secret, string hash) => hash == Hash(secret);
    public string Lookup(string pin) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pepper:" + pin)));
}
