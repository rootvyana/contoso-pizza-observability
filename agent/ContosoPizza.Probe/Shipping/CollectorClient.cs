using System.Net.Http.Json;
using System.Text.Json;
using ContosoPizza.Shared.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContosoPizza.Probe.Shipping;

/// <summary>
/// The probe's one outbound dependency: POST a batch of flows to the collector
/// on the LAN and apply whatever configuration comes back.
///
/// Unsent flows are held in a bounded in-memory buffer and retried on the next
/// scrape. Nothing is written to disk. See ProbeOptions.MaxBufferedFlows for
/// why that asymmetry with the collector is deliberate rather than an omission.
/// </summary>
public sealed class CollectorClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProbeOptions _options;
    private readonly ProbeIdentity _identity;
    private readonly ILogger<CollectorClient> _logger;

    private readonly Queue<ProbeFlow> _pending = new();
    private readonly Lock _gate = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public CollectorClient(
        IHttpClientFactory httpClientFactory,
        IOptions<ProbeOptions> options,
        ProbeIdentity identity,
        ILogger<CollectorClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _identity = identity;
        _logger = logger;
    }

    public string FlowsUrl => $"{_options.CollectorEndpoint.TrimEnd('/')}/v1/flows";

    /// <summary>Scrape interval last handed down by the collector.</summary>
    public TimeSpan ScrapeInterval { get; private set; }

    public bool CollectionEnabled { get; private set; } = true;

    public bool Connected { get; private set; }

    public string? LastError { get; private set; }

    public int Buffered
    {
        get { lock (_gate) { return _pending.Count; } }
    }

    public long Dropped { get; private set; }

    public long Delivered { get; private set; }

    /// <summary>
    /// Queue flows and try to deliver everything outstanding.
    ///
    /// Returns false when the collector could not be reached, which the caller
    /// reports but does not act on: the flows stay buffered and the next scrape
    /// tries again.
    /// </summary>
    public async Task<bool> SendAsync(
        IReadOnlyList<ProbeFlow> flows, ProbeCounters counters, CancellationToken ct)
    {
        ProbeFlow[] batch;

        lock (_gate)
        {
            foreach (var flow in flows)
            {
                _pending.Enqueue(flow);
            }

            // Newest wins. An operator looking at a fleet view during an outage
            // is asking what is happening now, not what happened an hour ago.
            while (_pending.Count > _options.MaxBufferedFlows)
            {
                _pending.Dequeue();
                Dropped++;
            }

            if (_pending.Count == 0)
            {
                batch = [];
            }
            else
            {
                batch = [.. _pending];
            }
        }

        // An empty batch still goes, because the POST is also how the probe
        // learns about a configuration change and how the collector learns the
        // probe is alive. A quiet server must not look like a dead one.
        var payload = new ProbeBatch
        {
            Server = _options.ResolveServerName(),
            ProbeId = _identity.ProbeId,
            ProbeVersion = ProbeVersion,
            SentAt = DateTimeOffset.UtcNow,
            Flows = batch,
            Counters = counters,
        };

        try
        {
            var client = _httpClientFactory.CreateClient("collector");

            using var request = new HttpRequestMessage(HttpMethod.Post, FlowsUrl)
            {
                Content = JsonContent.Create(payload, options: Json),
            };

            if (_options.ResolveProbeKey() is { Length: > 0 } key)
            {
                request.Headers.TryAddWithoutValidation("X-Probe-Key", key);
            }

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Fail($"HTTP {(int)response.StatusCode}: {Truncate(body)}");

                // 401/403 will not fix itself by retrying, and holding the flows
                // would fill the buffer with data that can never be delivered.
                // Drop them and keep saying why, loudly, on every scrape.
                if ((int)response.StatusCode is 401 or 403)
                {
                    lock (_gate)
                    {
                        Dropped += _pending.Count;
                        _pending.Clear();
                    }

                    _logger.LogError(
                        "Collector rejected this probe ({Status}). Check that X-Probe-Key matches "
                      + "the collector's Probe:Key (or CONTOSO_PROBE_KEY).", (int)response.StatusCode);
                }

                return false;
            }

            var ack = await response.Content
                .ReadFromJsonAsync<ProbeAck>(Json, ct).ConfigureAwait(false);

            lock (_gate)
            {
                // Only what was actually in the batch is cleared: a scrape that
                // finished while the POST was in flight must not be discarded
                // along with it.
                for (int i = 0; i < batch.Length && _pending.Count > 0; i++)
                {
                    _pending.Dequeue();
                }
            }

            Delivered += batch.Length;
            Connected = true;
            LastError = null;
            ApplyAck(ack);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Fail(ex.Message);
            return false;
        }
    }

    private void ApplyAck(ProbeAck? ack)
    {
        if (ack is null)
        {
            return;
        }

        CollectionEnabled = ack.CollectionEnabled;

        if (ack.ScrapeIntervalMs <= 0)
        {
            return;
        }

        // Clamped here as well as in the dashboard and the collector. Three
        // clamps is not paranoia: this is the one that runs on the customer's
        // server, and it is the only one that is still true if the other two
        // are bypassed by a bug.
        int clamped = Math.Clamp(ack.ScrapeIntervalMs, 250, 300_000);
        var interval = TimeSpan.FromMilliseconds(clamped);

        if (interval != ScrapeInterval)
        {
            _logger.LogInformation("Collector set the scrape interval to {Interval}", interval);
            ScrapeInterval = interval;
        }
    }

    private void Fail(string message)
    {
        Connected = false;

        // Logged at warning once per transition rather than every scrape: an
        // overnight collector outage should not produce 20,000 identical lines.
        if (LastError != message)
        {
            _logger.LogWarning("Cannot reach the collector at {Url}: {Error}", FlowsUrl, message);
        }

        LastError = message;
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "...";

    public const string ProbeVersion = "0.2.0";
}
