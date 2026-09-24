using System.Collections.Concurrent;

namespace ContosoPizza.Collector.Fleet;

/// <summary>
/// Which servers have reported through this collector, and when.
///
/// The collector is the only thing that can answer "is the fleet reporting?",
/// because it is the only thing that sees all of it. A per-server agent talking
/// straight to the cloud can tell you that *it* is alive; nothing can tell you
/// that the server which stopped reporting yesterday exists at all.
/// </summary>
public sealed class FleetRegistry
{
    private readonly ConcurrentDictionary<string, ServerEntry> _servers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Capped, because the key arrives from the network. An unbounded dictionary
    /// fed by a misconfigured probe that randomises its hostname is a slow leak
    /// in a process meant to run for months.
    /// </summary>
    private const int MaxServers = 500;

    public sealed record ServerEntry(
        string Server,
        string? ProbeId,
        string? ProbeVersion,
        DateTimeOffset FirstSeen,
        DateTimeOffset LastSeen,
        long FlowsReceived);

    public void Record(string server, string? probeId, string? probeVersion, int flows)
    {
        server = server.Trim();

        if (server.Length == 0)
        {
            return;
        }

        if (server.Length > 200)
        {
            server = server[..200];
        }

        var now = DateTimeOffset.UtcNow;

        _servers.AddOrUpdate(
            server,
            _ => _servers.Count >= MaxServers
                ? new ServerEntry(server, probeId, probeVersion, now, now, flows)
                : new ServerEntry(server, probeId, probeVersion, now, now, flows),
            (_, existing) => existing with
            {
                ProbeId = probeId ?? existing.ProbeId,
                ProbeVersion = probeVersion ?? existing.ProbeVersion,
                LastSeen = now,
                FlowsReceived = existing.FlowsReceived + flows,
            });

        // Trimmed after the fact rather than refusing the write: the newest
        // server is the one somebody just installed and is watching for.
        while (_servers.Count > MaxServers)
        {
            var oldest = _servers.Values.OrderBy(s => s.LastSeen).FirstOrDefault();

            if (oldest is null || !_servers.TryRemove(oldest.Server, out _))
            {
                break;
            }
        }
    }

    public IReadOnlyList<string> ServerNames() =>
        [.. _servers.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<ServerEntry> Servers() =>
        [.. _servers.Values.OrderByDescending(s => s.LastSeen)];

    public int Count => _servers.Count;
}
