using System.Security.Cryptography;
using System.Text;
using Tausi.Application.Events;
using Tausi.Application.Security;

namespace Tausi.Tests.Support;

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

    /// <summary>Runs as each event is published, e.g. to check what the database holds at that moment.</summary>
    public Action<string>? OnPublish { get; set; }

    public Task Publish(string eventName, object payload, CancellationToken ct = default)
    {
        Published.Add((eventName, payload));
        OnPublish?.Invoke(eventName);
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

/// <summary>Files kept in memory for the duration of a test.</summary>
public sealed class MemoryFileStorage : Tausi.Application.Files.IFileStorage
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte[]> _files = new();

    public async Task Save(Guid fileId, Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        _files[fileId] = buffer.ToArray();
    }

    public Stream? Open(Guid fileId) => _files.TryGetValue(fileId, out var bytes) ? new MemoryStream(bytes) : null;
}
