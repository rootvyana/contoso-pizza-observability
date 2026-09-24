using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContosoPizza.Collector.Cloud;
using ContosoPizza.Shared.Wire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContosoPizza.Collector.Spool;

/// <summary>
/// Drains the spool to loveheartbeat.com, deleting each segment only once the
/// server has accepted it.
///
/// This is the only thing in the whole system that talks to the cloud with
/// data. Outbound only, one direction, durable across an outage -- and one
/// credential for the entire fleet behind it.
/// </summary>
public sealed class SpoolPump : BackgroundService
{
    private readonly EventSpool _spool;
    private readonly CollectorState _state;
    private readonly CloudSession _session;
    private readonly CloudOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<SpoolPump> _logger;

    private TimeSpan _backoff = TimeSpan.Zero;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SpoolPump(
        EventSpool spool,
        CollectorState state,
        CloudSession session,
        IOptions<CloudOptions> options,
        HttpClient http,
        ILogger<SpoolPump> logger)
    {
        _spool = spool;
        _state = state;
        _session = session;
        _options = options.Value;
        _http = http;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Spool pump started, delivering to {Url}", _options.TelemetryUrl);

        using var timer = new PeriodicTimer(_options.FlushInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await PumpOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spool pump iteration failed");
            }
        }

        // Best-effort final drain so a clean shutdown does not strand records.
        _spool.Roll();
        try
        {
            await PumpOnceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final drain failed; records remain spooled for next start");
        }
    }

    /// <summary>
    /// One drain cycle. Internal rather than private so a flush command and the
    /// --selftest diagnostic can force delivery instead of waiting for a tick.
    /// </summary>
    internal async Task PumpOnceAsync(CancellationToken ct)
    {
        _spool.Roll();

        _state.AddSegmentsDropped(_spool.EnforceQuota());

        var segments = _spool.ReadySegments();
        _state.SpoolSegmentsPending = segments.Count;
        _state.SpoolBytes = _spool.TotalBytes;

        if (segments.Count == 0)
        {
            return;
        }

        if (_backoff > TimeSpan.Zero)
        {
            await Task.Delay(_backoff, ct).ConfigureAwait(false);
        }

        foreach (string segment in segments)
        {
            string[] lines;
            try
            {
                lines = EventSpool.ReadSegment(segment);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not read segment {Segment}; skipping", segment);
                continue;
            }

            if (lines.Length == 0)
            {
                _spool.Complete(segment);
                continue;
            }

            bool allAccepted = true;

            foreach (string[] chunk in lines.Chunk(Math.Clamp(_options.BatchSize, 1, 200)))
            {
                if (!await TrySendAsync(chunk, ct).ConfigureAwait(false))
                {
                    allAccepted = false;
                    break;
                }
            }

            if (!allAccepted)
            {
                // Leave this and every later segment in place: ordering is
                // preserved and nothing is lost.
                return;
            }

            _spool.Complete(segment);
            _state.AddRecordsDelivered(lines.Length);
            _state.LastSuccessfulDelivery = DateTimeOffset.UtcNow;
            _state.LastDeliveryError = null;
            _backoff = TimeSpan.Zero;

            _logger.LogDebug("Delivered {Count} record(s) from {Segment}", lines.Length, segment);
        }
    }

    private async Task<bool> TrySendAsync(IReadOnlyCollection<string> lines, CancellationToken ct)
    {
        var batch = BuildBatch(lines);

        if (batch is null)
        {
            // Every line in the chunk was unparseable. Nothing to send, and
            // nothing to be gained by retrying it.
            return true;
        }

        string? session = await _session.GetSessionAsync(ct).ConfigureAwait(false);

        if (session is null)
        {
            RecordFailure("no cloud session");
            return false;
        }

        var outcome = await PostAsync(batch, session, ct).ConfigureAwait(false);

        // One retry, and only for 401. A session can lapse between the check
        // above and the request arriving; re-minting costs a round trip, and
        // not retrying would cost the batch.
        if (outcome == Outcome.Unauthorized)
        {
            _logger.LogInformation("Session refused; renewing and retrying once");
            _session.Invalidate();

            session = await _session.GetSessionAsync(ct).ConfigureAwait(false);

            if (session is null)
            {
                RecordFailure("could not renew the cloud session");
                return false;
            }

            outcome = await PostAsync(batch, session, ct).ConfigureAwait(false);
        }

        return outcome == Outcome.Accepted;
    }

    private enum Outcome
    {
        Accepted,
        Unauthorized,
        Retry,
    }

    private async Task<Outcome> PostAsync(
        CloudTelemetryBatch batch, string session, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.TelemetryUrl)
            {
                Content = JsonContent.Create(batch, options: Json),
            };

            CloudSession.Authorize(request, session);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return Outcome.Accepted;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Outcome.Unauthorized;
            }

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            RecordFailure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}");

            // 4xx other than 401/408/429 will not succeed on retry with the
            // same payload. Dropping is the lesser evil: otherwise one
            // malformed batch blocks the queue behind it forever.
            if (IsPermanent(response.StatusCode))
            {
                _logger.LogError(
                    "Server rejected the batch permanently ({Status}); discarding it",
                    (int)response.StatusCode);
                return Outcome.Accepted;
            }

            return Outcome.Retry;
        }
        catch (HttpRequestException ex)
        {
            RecordFailure(ex.Message);
            return Outcome.Retry;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            RecordFailure($"timed out: {ex.Message}");
            return Outcome.Retry;
        }
    }

    /// <summary>
    /// Regroup spooled lines into the three lists the cloud intake expects.
    ///
    /// The spool is one ordered file of mixed records, and the wire format is
    /// three arrays. Grouping happens here, at the last possible moment, so the
    /// spool keeps a single ordering rather than three that can drain unevenly.
    /// </summary>
    private static CloudTelemetryBatch? BuildBatch(IReadOnlyCollection<string> lines)
    {
        var logs = new List<Dictionary<string, object?>>();
        var spans = new List<Dictionary<string, object?>>();
        var flows = new List<Dictionary<string, object?>>();

        foreach (string line in lines)
        {
            var parsed = SpoolWriter.Parse(line);

            if (parsed is null)
            {
                continue;
            }

            switch (parsed.Kind)
            {
                case SpooledRecord.KindSpan:
                    spans.Add(parsed.Record);
                    break;
                case SpooledRecord.KindFlow:
                    flows.Add(parsed.Record);
                    break;
                default:
                    logs.Add(parsed.Record);
                    break;
            }
        }

        if (logs.Count == 0 && spans.Count == 0 && flows.Count == 0)
        {
            return null;
        }

        return new CloudTelemetryBatch { Logs = logs, Spans = spans, Flows = flows };
    }

    private static bool IsPermanent(HttpStatusCode status) =>
        (int)status is >= 400 and < 500
        && status is not HttpStatusCode.Unauthorized
        && status is not HttpStatusCode.RequestTimeout
        && status is not HttpStatusCode.TooManyRequests;

    private void RecordFailure(string message)
    {
        _state.IncrementDeliveryFailures();
        _state.LastDeliveryError = message;

        _backoff = _backoff == TimeSpan.Zero
            ? _options.ReconnectMinDelay
            : TimeSpan.FromMilliseconds(Math.Min(
                _backoff.TotalMilliseconds * 2,
                _options.ReconnectMaxDelay.TotalMilliseconds));

        _logger.LogWarning("Delivery failed ({Message}); backing off {Backoff}", message, _backoff);
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "...";
}
