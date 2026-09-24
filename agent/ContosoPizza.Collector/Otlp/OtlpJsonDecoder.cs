using System.Text.Json;

namespace ContosoPizza.Collector.Otlp;

/// <summary>
/// OTLP/HTTP with JSON encoding.
///
/// Less common than protobuf -- no OpenTelemetry SDK emits it by default -- but
/// it is what a hand-rolled exporter, a curl reproduction and the OTel
/// Collector's `encoding: json` produce, and rejecting it would turn a working
/// pipeline into a support ticket.
///
/// The one trap in OTLP/JSON is proto3's canonical mapping: a 64-bit integer is
/// encoded as a *string*, because JSON numbers cannot hold int64 exactly. So
/// startTimeUnixNano arrives as "1700000000000000000", intValue arrives as
/// "500", and a decoder that expects numbers silently reads zero for every
/// timestamp.
/// </summary>
internal static class OtlpJsonDecoder
{
    public static List<OtlpSpan> DecodeTraces(JsonElement root)
    {
        var spans = new List<OtlpSpan>();

        foreach (JsonElement resourceSpans in Array(root, "resourceSpans"))
        {
            var resource = ReadResourceAttributes(resourceSpans);

            foreach (JsonElement scopeSpans in Array(resourceSpans, "scopeSpans"))
            {
                foreach (JsonElement span in Array(scopeSpans, "spans"))
                {
                    spans.Add(ReadSpan(span, resource));
                }
            }
        }

        return spans;
    }

    public static List<OtlpLog> DecodeLogs(JsonElement root)
    {
        var logs = new List<OtlpLog>();

        foreach (JsonElement resourceLogs in Array(root, "resourceLogs"))
        {
            var resource = ReadResourceAttributes(resourceLogs);

            foreach (JsonElement scopeLogs in Array(resourceLogs, "scopeLogs"))
            {
                foreach (JsonElement record in Array(scopeLogs, "logRecords"))
                {
                    logs.Add(ReadLog(record, resource));
                }
            }
        }

        return logs;
    }

    public static int CountMetricPoints(JsonElement root)
    {
        int count = 0;

        foreach (JsonElement resourceMetrics in Array(root, "resourceMetrics"))
        {
            foreach (JsonElement scopeMetrics in Array(resourceMetrics, "scopeMetrics"))
            {
                count += Array(scopeMetrics, "metrics").Count();
            }
        }

        return count;
    }

    private static OtlpSpan ReadSpan(JsonElement span, Dictionary<string, object?> resource)
    {
        var attributes = new Dictionary<string, object?>(resource);
        ReadAttributesInto(span, attributes);

        return new OtlpSpan
        {
            TraceId = String(span, "traceId") ?? "",
            SpanId = String(span, "spanId") ?? "",
            ParentSpanId = String(span, "parentSpanId") is { Length: > 0 } parent ? parent : null,
            Name = String(span, "name") ?? "",
            Kind = OtlpNames.SpanKind(Int32(span, "kind")),
            StartUnixNano = UInt64(span, "startTimeUnixNano"),
            EndUnixNano = UInt64(span, "endTimeUnixNano"),
            Status = span.TryGetProperty("status", out JsonElement status)
                ? OtlpNames.StatusCode(Int32(status, "code"))
                : "UNSET",
            Attributes = attributes,
        };
    }

    private static OtlpLog ReadLog(JsonElement record, Dictionary<string, object?> resource)
    {
        var attributes = new Dictionary<string, object?>(resource);
        ReadAttributesInto(record, attributes);

        ulong time = UInt64(record, "timeUnixNano");

        return new OtlpLog
        {
            TimeUnixNano = time != 0 ? time : UInt64(record, "observedTimeUnixNano"),
            Level = OtlpNames.Severity(Int32(record, "severityNumber"), String(record, "severityText")),
            Message = record.TryGetProperty("body", out JsonElement body)
                ? AnyValueToString(body)
                : "",
            TraceId = String(record, "traceId") is { Length: > 0 } t ? t : null,
            SpanId = String(record, "spanId") is { Length: > 0 } s ? s : null,
            Attributes = attributes,
        };
    }

    private static Dictionary<string, object?> ReadResourceAttributes(JsonElement parent)
    {
        var attributes = new Dictionary<string, object?>();

        if (parent.TryGetProperty("resource", out JsonElement resource))
        {
            ReadAttributesInto(resource, attributes);
        }

        return attributes;
    }

    private static void ReadAttributesInto(JsonElement parent, Dictionary<string, object?> into)
    {
        foreach (JsonElement attribute in Array(parent, "attributes"))
        {
            if (String(attribute, "key") is not { Length: > 0 } key)
            {
                continue;
            }

            into[key] = attribute.TryGetProperty("value", out JsonElement value)
                ? AnyValue(value)
                : null;
        }
    }

    private static object? AnyValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (value.TryGetProperty("stringValue", out JsonElement s))
        {
            return s.GetString();
        }

        if (value.TryGetProperty("boolValue", out JsonElement b))
        {
            return b.ValueKind == JsonValueKind.True
                || (b.ValueKind == JsonValueKind.String && bool.TryParse(b.GetString(), out bool parsed) && parsed);
        }

        // The int64-as-string rule. A number is accepted too, because a
        // hand-written producer will send one and refusing it would be
        // pedantry at the cost of the data.
        if (value.TryGetProperty("intValue", out JsonElement i))
        {
            return i.ValueKind switch
            {
                JsonValueKind.String when long.TryParse(i.GetString(), out long parsed) => parsed,
                JsonValueKind.Number when i.TryGetInt64(out long number) => number,
                _ => null,
            };
        }

        if (value.TryGetProperty("doubleValue", out JsonElement d))
        {
            return d.ValueKind switch
            {
                JsonValueKind.Number => d.GetDouble(),
                JsonValueKind.String when double.TryParse(
                    d.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed) => parsed,
                _ => null,
            };
        }

        if (value.TryGetProperty("bytesValue", out JsonElement bytes))
        {
            return bytes.GetString();
        }

        if (value.TryGetProperty("arrayValue", out _) || value.TryGetProperty("kvlistValue", out _))
        {
            return "(structured)";
        }

        return null;
    }

    private static string AnyValueToString(JsonElement body) =>
        AnyValue(body) switch
        {
            null => "",
            string s => s,
            bool b => b ? "true" : "false",
            var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement array)
        && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    private static string? String(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int32(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out int parsed) => parsed,
            _ => 0,
        };
    }

    private static ulong UInt64(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String when ulong.TryParse(value.GetString(), out ulong parsed) => parsed,
            JsonValueKind.Number when value.TryGetUInt64(out ulong number) => number,
            _ => 0,
        };
    }
}
