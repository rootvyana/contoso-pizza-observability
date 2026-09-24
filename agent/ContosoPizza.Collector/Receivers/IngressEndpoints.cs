using System.Text.Json;
using ContosoPizza.Collector.Cloud;
using ContosoPizza.Collector.Fleet;
using ContosoPizza.Collector.Otlp;
using ContosoPizza.Collector.Spool;
using ContosoPizza.Shared.Wire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ContosoPizza.Collector.Receivers;

/// <summary>
/// The LAN-facing side of the collector: what probes and applications post to.
///
/// Two producers, one queue. A probe sends kernel flows; an application sends
/// OpenTelemetry exactly as it would to any OTLP endpoint, with nothing in the
/// app changed but the URL it exports to. Both land in the same spool and leave
/// on the same authenticated connection to the cloud.
///
/// Nothing here is reachable from the internet. The collector listens on the
/// customer's network only, and its own connection outward is what crosses the
/// boundary -- the Power BI gateway's rule, kept.
/// </summary>
internal static class IngressEndpoints
{
    public static void MapIngress(this WebApplication app)
    {
        app.MapPost("/v1/flows", ReceiveFlows);

        // The standard OTLP/HTTP paths, so an exporter configured with this
        // collector's base URL works with no other change. The SDK appends
        // these itself.
        //
        // Cast to Delegate deliberately: a lambda that takes only HttpContext
        // binds as a RequestDelegate, which discards the returned IResult and
        // answers every export with an empty 200 body.
        app.MapPost("/v1/traces", (Delegate)ReceiveTraces);
        app.MapPost("/v1/logs", (Delegate)ReceiveLogs);
        app.MapPost("/v1/metrics", (Delegate)ReceiveMetrics);

        app.MapGet("/health", (CollectorState state) => Results.Ok(new
        {
            status = state.CloudAuthenticated ? "healthy" : "not_authenticated",
            uptimeSeconds = (long)state.Uptime.TotalSeconds,
        }));

        // The local answer to "is this thing working", for somebody standing on
        // the collector's own machine with no dashboard access.
        app.MapGet("/status", (CollectorState state, FleetRegistry fleet, CloudSession session) =>
            Results.Ok(new
            {
                collectorId = session.CollectorId,
                enrolled = session.Enrolled,
                authenticated = state.CloudAuthenticated,
                servers = fleet.Servers(),
                health = state.Health(),
            }));
    }

    private static Task<IResult> ReceiveTraces(HttpContext http) =>
        ReceiveOtlp(http, OtlpSignal.Traces);

    private static Task<IResult> ReceiveLogs(HttpContext http) =>
        ReceiveOtlp(http, OtlpSignal.Logs);

    private static Task<IResult> ReceiveMetrics(HttpContext http) =>
        ReceiveOtlp(http, OtlpSignal.Metrics);

    private enum OtlpSignal
    {
        Traces,
        Logs,
        Metrics,
    }

    // --- probes ---------------------------------------------------------------

    private static IResult ReceiveFlows(
        [FromBody] ProbeBatch batch,
        HttpContext http,
        SpoolWriter spool,
        CollectorState state,
        FleetRegistry fleet,
        IOptions<IngressOptions> ingress,
        ILoggerFactory loggerFactory)
    {
        if (!Authorized(http, ingress.Value))
        {
            return Results.Json(new { error = "invalid probe key" }, statusCode: 401);
        }

        string server = string.IsNullOrWhiteSpace(batch.Server) ? "unknown" : batch.Server.Trim();

        foreach (var flow in batch.Flows)
        {
            spool.WriteFlow(ToCloudFlow(server, flow));
        }

        state.AddFlowsReceived(batch.Flows.Count);
        fleet.Record(server, batch.ProbeId, batch.ProbeVersion, batch.Flows.Count);

        if (batch.Flows.Count > 0)
        {
            loggerFactory.CreateLogger("ingress").LogDebug(
                "Accepted {Count} flow(s) from {Server}", batch.Flows.Count, server);
        }

        // The ack carries configuration back down, which is how one change in
        // the dashboard reaches every server without anybody logging into one.
        return Results.Ok(new ProbeAck
        {
            Accepted = batch.Flows.Count,
            ScrapeIntervalMs = state.ScrapeIntervalMs,
            CollectionEnabled = state.CollectionEnabled,
        });
    }

    /// <summary>
    /// A probe's flow in the field names collectors.py reads. snake_case
    /// because that is the contract on the far side, converted once, here,
    /// rather than leaking Python's naming into the probe's own wire format.
    /// </summary>
    private static Dictionary<string, object?> ToCloudFlow(string server, ProbeFlow flow) => new()
    {
        ["id"] = flow.Id,
        ["timestamp"] = flow.ObservedAt.ToString("O"),
        ["server"] = server,
        ["direction"] = flow.Direction,
        ["local_addr"] = flow.LocalAddress,
        ["local_port"] = flow.LocalPort,
        ["remote_addr"] = flow.RemoteAddress,
        ["remote_port"] = flow.RemotePort,
        ["state"] = flow.State,
        ["duration_ms"] = Math.Round(flow.DurationMs, 3),
        ["process_id"] = flow.ProcessId,
        ["process_name"] = flow.ProcessName,
        // The join key back into the application's spans, which carry
        // client.port. Present on inbound rows only -- on an outbound row the
        // remote port is the server's and joins nothing.
        ["client_port"] = flow.ClientPort,
    };

    // --- OpenTelemetry --------------------------------------------------------

    private static async Task<IResult> ReceiveOtlp(HttpContext http, OtlpSignal signal)
    {
        var services = http.RequestServices;
        var ingress = services.GetRequiredService<IOptions<IngressOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("otlp");

        if (ingress.RequireKeyForOtlp && !Authorized(http, ingress))
        {
            return Results.Json(new { error = "invalid probe key" }, statusCode: 401);
        }

        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await http.Request.Body.CopyToAsync(buffer, http.RequestAborted).ConfigureAwait(false);
            body = buffer.ToArray();
        }

        if (body.Length == 0)
        {
            // An empty export is normal exporter behaviour, not an error.
            return OtlpAccepted();
        }

        var spool = services.GetRequiredService<SpoolWriter>();
        var state = services.GetRequiredService<CollectorState>();
        var fleet = services.GetRequiredService<FleetRegistry>();

        string contentType = (http.Request.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        bool isProtobuf = contentType is "application/x-protobuf" or "application/protobuf";

        try
        {
            switch (signal)
            {
                case OtlpSignal.Traces:
                {
                    var spans = isProtobuf
                        ? OtlpProtobufDecoder.DecodeTraces(body)
                        : OtlpJsonDecoder.DecodeTraces(Parse(body));

                    foreach (var span in spans)
                    {
                        string? server = span.Server();
                        spool.WriteSpan(span.ToRecord(server));

                        if (server is not null)
                        {
                            fleet.Record(server, probeId: null, probeVersion: null, flows: 0);
                        }
                    }

                    state.AddSpansReceived(spans.Count);
                    break;
                }

                case OtlpSignal.Logs:
                {
                    var logs = isProtobuf
                        ? OtlpProtobufDecoder.DecodeLogs(body)
                        : OtlpJsonDecoder.DecodeLogs(Parse(body));

                    foreach (var log in logs)
                    {
                        spool.WriteLog(log.ToRecord(log.Server()));
                    }

                    state.AddLogsReceived(logs.Count);
                    break;
                }

                case OtlpSignal.Metrics:
                {
                    // Accepted and counted, not stored. The cloud intake takes
                    // logs, spans and flows; returning an error here would make
                    // a correctly configured exporter retry forever, and
                    // returning success silently would lose the metrics without
                    // anybody noticing. The count surfaces on the collector's
                    // health as otlp_metrics_dropped.
                    int points = isProtobuf
                        ? OtlpProtobufDecoder.CountMetricPoints(body)
                        : OtlpJsonDecoder.CountMetricPoints(Parse(body));

                    state.AddMetricsDropped(points);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            logger.LogWarning(
                "Rejected an OTLP {Signal} export ({ContentType}): {Error}",
                signal, contentType, ex.Message);

            return Results.Json(
                new { error = $"Body is not a valid OTLP {signal} export: {ex.Message}" },
                statusCode: 400);
        }

        return OtlpAccepted();
    }

    private static JsonElement Parse(byte[] body) =>
        JsonDocument.Parse(body).RootElement;

    /// <summary>
    /// OTLP's success shape. An exporter reads partialSuccess to decide whether
    /// to retry, so an empty one means "all of it, keep going".
    /// </summary>
    private static IResult OtlpAccepted() =>
        Results.Json(new { partialSuccess = new { } });

    private static bool Authorized(HttpContext http, IngressOptions options)
    {
        string expected = options.ResolveProbeKey();

        if (expected.Length == 0)
        {
            // No key configured accepts everything. Warned about at start-up
            // rather than here, so an open collector is noticed once on the
            // console instead of never.
            return true;
        }

        string presented = http.Request.Headers["X-Probe-Key"].ToString();

        return presented.Length > 0
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(presented),
                System.Text.Encoding.UTF8.GetBytes(expected));
    }
}
