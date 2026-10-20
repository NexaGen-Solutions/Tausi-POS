using System.Collections.Concurrent;

namespace Tausi.Application.Terminals;

/// <summary>
/// Which terminals are online. The terminal app's live connection is the heartbeat: a terminal is
/// online while connected, and counts as offline <see cref="Grace"/> after its last connection
/// closes (so a quick reconnect doesn't flicker).
/// </summary>
public sealed class TerminalPresence(TimeProvider clock)
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Guid> _connections = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastDisconnect = new();
    private readonly ConcurrentDictionary<Guid, bool> _published = new();

    public void Connected(Guid terminalId, string connectionId)
    {
        _connections[connectionId] = terminalId;
        _lastDisconnect.TryRemove(terminalId, out _);
    }

    public void Disconnected(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var terminalId)) return;
        if (!_connections.Values.Contains(terminalId)) _lastDisconnect[terminalId] = clock.GetUtcNow();
    }

    public bool IsOnline(Guid terminalId) =>
        _connections.Values.Contains(terminalId)
        || (_lastDisconnect.TryGetValue(terminalId, out var at) && clock.GetUtcNow() - at < Grace);

    /// <summary>When the terminal was last connected, if it disconnected since the server started.</summary>
    public DateTimeOffset? LastDisconnect(Guid terminalId) =>
        _lastDisconnect.TryGetValue(terminalId, out var at) ? at : null;

    public IReadOnlyList<string> ConnectionsOf(Guid terminalId) =>
        _connections.Where(kv => kv.Value == terminalId).Select(kv => kv.Key).ToList();

    /// <summary>Terminals whose online state changed since the last call, for the terminal.status event.</summary>
    public IReadOnlyList<(Guid TerminalId, bool Online)> TakeChanges()
    {
        var known = _connections.Values.Concat(_lastDisconnect.Keys).Concat(_published.Keys).Distinct().ToList();
        var changes = new List<(Guid, bool)>();
        foreach (var id in known)
        {
            var online = IsOnline(id);
            if (_published.TryGetValue(id, out var was) && was == online) continue;
            _published[id] = online;
            changes.Add((id, online));
        }
        return changes;
    }
}
