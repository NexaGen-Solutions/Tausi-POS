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

/// <summary>NexaGen's update feed and its downloads, from memory (files are copied from local paths).</summary>
public sealed class FakeUpdateSource : Tausi.Application.Updates.IUpdateSource
{
    public Tausi.Application.Updates.UpdateFeed? Feed { get; set; }
    public Dictionary<string, string> Files { get; } = [];

    public Task<Tausi.Application.Updates.UpdateFeed> Fetch(CancellationToken ct) =>
        Feed is null ? throw new HttpRequestException("No internet.") : Task.FromResult(Feed);

    public Task Download(string url, string path, CancellationToken ct)
    {
        File.Copy(Files[url], path, overwrite: true);
        return Task.CompletedTask;
    }
}

/// <summary>Records the installer it would have run.</summary>
public sealed class FakeUpdateInstaller : Tausi.Application.Updates.IUpdateInstaller
{
    public string Platform => "windows-x64";
    public string? Why { get; set; }
    public string? CantInstall() => Why;
    public string DownloadFolder { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tausi-updates-" + Guid.NewGuid().ToString("N"));
    public List<(string File, string Version)> Launched { get; } = [];
    public void Launch(string installer, string version, string sha256, string signature) => Launched.Add((installer, version));
}

/// <summary>Other Tausi POS servers, by address: the business each one holds (none: it doesn't answer).</summary>
public sealed class FakeServerProbe : Tausi.Application.Terminals.IServerProbe
{
    public Dictionary<string, string> Servers { get; } = [];
    public Task<string?> BusinessName(string serverUrl, CancellationToken ct) => Task.FromResult(Servers.GetValueOrDefault(serverUrl));
}
