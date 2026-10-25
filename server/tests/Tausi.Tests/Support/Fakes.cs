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

/// <summary>setup-token.txt, in memory.</summary>
public sealed class MemoryTokenFile : Tausi.Application.Setup.ISetupTokenFile
{
    public string? Token { get; private set; }
    public string Path => "/data/setup-token.txt";
    public string? Read() => Token;
    public void Write(string token) => Token = token;
    public void Delete() => Token = null;
}

/// <summary>A SambaPOS database as data, for the import's mapping (the real reader is tested on SQL Server).</summary>
public sealed class FakeSambaPos : Tausi.Application.Setup.ISambaPosSource
{
    public Tausi.Application.Setup.SambaPosData Data { get; set; } = new([], [], [], [], [], [], [], [], [], []);

    public Task<string> Test(Tausi.Application.Setup.SambaPosConnection connection, CancellationToken ct) => Task.FromResult("Connected.");

    public Task<Tausi.Application.Setup.SambaPosData> Read(Tausi.Application.Setup.SambaPosConnection connection, CancellationToken ct) =>
        Task.FromResult(Data);
}
