using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContosoPizza.Collector.Fleet;
using ContosoPizza.Shared.Wire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContosoPizza.Collector.Cloud;

/// <summary>
/// Health up, configuration down, on a fixed interval.
///
/// This replaces the WebSocket control channel the previous design carried. A
/// WebSocket was the right shape for a server that can hold one; loveheartbeat
/// runs FastAPI under Mangum on Lambda, where the process exists only for the
/// duration of a request and cannot hold a socket open at all. Polling on an
/// interval the server itself chooses gets the same two things across --
/// "is this collector healthy" and "here is your new configuration" -- over
/// the transport that deployment actually has.
///
/// It also keeps the session permanently fresh as a side effect: every
/// heartbeat is an authenticated request, and the server slides the session
/// forward on each one, so a collector that is beating never has a session old
/// enough to expire.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    private readonly CloudSession _session;
    private readonly CollectorState _state;
    private readonly FleetRegistry _fleet;
    private readonly CloudOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<HeartbeatService> _logger;

    private TimeSpan _interval;
    private int _lastConfigVersion;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HeartbeatService(
        CloudSession session,
        CollectorState state,
        FleetRegistry fleet,
        IOptions<CloudOptions> options,
        HttpClient http,
        ILogger<HeartbeatService> logger)
    {
        _session = session;
        _state = state;
        _fleet = fleet;
        _options = options.Value;
        _http = http;
        _logger = logger;
        _interval = _options.HeartbeatInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Heartbeat every {Interval} to {Url}", _interval, _options.HeartbeatUrl);

        using var timer = new PeriodicTimer(_interval);

        await BeatAsync(stoppingToken).ConfigureAwait(false);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var applied = await BeatAsync(stoppingToken).ConfigureAwait(false);

            // The server owns the cadence, so a deployment can slow every
            // collector down without shipping a new build.
            if (applied > TimeSpan.Zero && applied != _interval)
            {
                _interval = applied;
                timer.Period = applied;
                _logger.LogInformation("Heartbeat interval now {Interval}", applied);
            }
        }
    }

    private async Task<TimeSpan> BeatAsync(CancellationToken ct)
    {
        string? session = await _session.GetSessionAsync(ct).ConfigureAwait(false);

        if (session is null)
        {
            // Not an error worth a stack trace: an unenrolled or offline
            // collector says so once per attempt through CloudSession, and
            // repeating it here would double every line.
            return TimeSpan.Zero;
        }

        var payload = new HeartbeatRequest
        {
            Hostname = Environment.MachineName,
            Version = CloudSession.CollectorVersion,
            Servers = _fleet.ServerNames(),
            Health = _state.Health(),
        };

        var response = await SendAsync(payload, session, ct).ConfigureAwait(false);

        if (response is null)
        {
            return TimeSpan.Zero;
        }

        _state.Apply(response.Config);

        if (response.ConfigVersion != _lastConfigVersion)
        {
            _lastConfigVersion = response.ConfigVersion;

            if (response.Config is not null)
            {
                _logger.LogInformation(
                    "Configuration v{Version} applied: scrape {Interval}ms, collection {Enabled}",
                    response.ConfigVersion,
                    _state.ScrapeIntervalMs,
                    _state.CollectionEnabled ? "enabled" : "paused");
            }
        }

        _state.ConfigVersion = response.ConfigVersion;

        return response.HeartbeatIntervalSeconds > 0
            ? TimeSpan.FromSeconds(response.HeartbeatIntervalSeconds)
            : TimeSpan.Zero;
    }

    private async Task<HeartbeatResponse?> SendAsync(
        HeartbeatRequest payload, string session, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.HeartbeatUrl)
                {
                    Content = JsonContent.Create(payload, options: Json),
                };

                CloudSession.Authorize(request, session);

                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content
                        .ReadFromJsonAsync<HeartbeatResponse>(Json, ct).ConfigureAwait(false);
                }

                // Same self-healing rule the pump uses: a refused session is
                // replaced from the durable credential and the call retried
                // once, so no operator ever has to restart a collector to fix
                // authentication.
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _session.Invalidate();
                    string? renewed = await _session.GetSessionAsync(ct).ConfigureAwait(false);

                    if (renewed is null)
                    {
                        return null;
                    }

                    session = renewed;
                    continue;
                }

                _logger.LogWarning("Heartbeat rejected ({Status})", (int)response.StatusCode);
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogDebug("Heartbeat could not be delivered: {Error}", ex.Message);
                return null;
            }
        }

        return null;
    }
}
