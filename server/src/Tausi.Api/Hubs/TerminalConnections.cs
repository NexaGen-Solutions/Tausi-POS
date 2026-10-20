using System.Collections.Concurrent;

namespace Tausi.Api.Hubs;

/// <summary>Which SignalR connections belong to which terminal.</summary>
public sealed class TerminalConnections
{
    private readonly ConcurrentDictionary<string, Guid> _byConnection = new();

    public void Add(Guid terminalId, string connectionId) => _byConnection[connectionId] = terminalId;

    public void Remove(string connectionId) => _byConnection.TryRemove(connectionId, out _);

    public IReadOnlyList<string> For(Guid terminalId) =>
        _byConnection.Where(kv => kv.Value == terminalId).Select(kv => kv.Key).ToList();
}
