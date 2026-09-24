using System.Globalization;

namespace ContosoPizza.Collector.Otlp;

/// <summary>
/// One decoded span, independent of which encoding it arrived in.
///
/// Protobuf and JSON both land here, so the conversion to the shape
/// loveheartbeat stores exists once rather than once per encoding -- two copies
/// of that mapping would be two chances for the JSON path and the protobuf path
/// to file the same span differently.
/// </summary>
internal sealed record OtlpSpan
{
    public required string TraceId { get; init; }

    public required string SpanId { get; init; }

    public string? ParentSpanId { get; init; }

    public required string Name { get; init; }

    public required string Kind { get; init; }

    public required ulong StartUnixNano { get; init; }

    public required ulong EndUnixNano { get; init; }

    public required string Status { get; init; }

    public required Dictionary<string, object?> Attributes { get; init; }

    public double DurationMs =>
        EndUnixNano > StartUnixNano ? (EndUnixNano - StartUnixNano) / 1_000_000d : 0d;

    /// <summary>
    /// The shape observability._normalize reads on the far side: span_id,
    /// trace_id, name, kind, start_time, duration_ms, status, attributes.
    /// </summary>
    public Dictionary<string, object?> ToRecord(string? server) => new()
    {
        ["span_id"] = SpanId,
        ["trace_id"] = TraceId,
        ["parent_span_id"] = ParentSpanId,
        ["name"] = Name,
        ["kind"] = Kind,
        ["start_time"] = OtlpNames.Timestamp(StartUnixNano),
        ["duration_ms"] = Math.Round(DurationMs, 3),
        ["status"] = Status,
        ["server"] = server,
        ["attributes"] = Attributes,
    };

    /// <summary>
    /// Which machine produced this. host.name first because that is what the
    /// eBPF flows are keyed by, so a span and a flow from the same box land
    /// under the same name and can be looked at together.
    /// </summary>
    public string? Server() => OtlpNames.Server(Attributes);
}

internal sealed record OtlpLog
{
    public required ulong TimeUnixNano { get; init; }

    public required string Level { get; init; }

    public required string Message { get; init; }

    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public required Dictionary<string, object?> Attributes { get; init; }

    public Dictionary<string, object?> ToRecord(string? server) => new()
    {
        ["timestamp"] = OtlpNames.Timestamp(TimeUnixNano),
        ["level"] = Level,
        ["message"] = Message,
        ["trace_id"] = TraceId,
        ["span_id"] = SpanId,
        ["source"] = "otel",
        ["server"] = server,
        ["attributes"] = Attributes,
    };

    public string? Server() => OtlpNames.Server(Attributes);
}

internal static class OtlpNames
{
    public static string SpanKind(int kind) => kind switch
    {
        1 => "INTERNAL",
        2 => "SERVER",
        3 => "CLIENT",
        4 => "PRODUCER",
        5 => "CONSUMER",
        _ => "INTERNAL",
    };

    public static string StatusCode(int code) => code switch
    {
        1 => "OK",
        2 => "ERROR",
        _ => "UNSET",
    };

    /// <summary>
    /// OTLP severity numbers are banded: 1-4 TRACE, 5-8 DEBUG, 9-12 INFO,
    /// 13-16 WARN, 17-20 ERROR, 21-24 FATAL. The text is preferred when the SDK
    /// sent one, because it is what the developer wrote.
    /// </summary>
    public static string Severity(int number, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text.Trim().ToUpperInvariant();
        }

        return number switch
        {
            >= 1 and <= 4 => "TRACE",
            >= 5 and <= 8 => "DEBUG",
            >= 9 and <= 12 => "INFO",
            >= 13 and <= 16 => "WARN",
            >= 17 and <= 20 => "ERROR",
            >= 21 => "FATAL",
            _ => "INFO",
        };
    }

    /// <summary>
    /// Unix nanoseconds to an ISO instant. Zero becomes now rather than 1970:
    /// an SDK that omitted the timestamp produced a record that happened
    /// roughly when it arrived, and filing it at the epoch hides it from every
    /// time-windowed query the dashboard runs.
    /// </summary>
    public static string Timestamp(ulong unixNano)
    {
        if (unixNano == 0)
        {
            return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }

        long milliseconds = (long)(unixNano / 1_000_000);

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            .ToString("O", CultureInfo.InvariantCulture);
    }

    public static string? Server(Dictionary<string, object?> attributes)
    {
        foreach (string key in (string[])["host.name", "service.instance.id", "service.name"])
        {
            if (attributes.TryGetValue(key, out object? value) &&
                value?.ToString() is { Length: > 0 } text)
            {
                // service.instance.id is "<machine>:<pid>" in this app's
                // resource; the machine half is the part that matches a flow.
                int colon = key == "service.instance.id" ? text.IndexOf(':') : -1;
                return colon > 0 ? text[..colon] : text;
            }
        }

        return null;
    }
}
