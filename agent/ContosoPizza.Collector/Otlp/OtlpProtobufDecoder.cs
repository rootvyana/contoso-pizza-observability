namespace ContosoPizza.Collector.Otlp;

/// <summary>
/// OTLP/HTTP protobuf -> the record shapes loveheartbeat stores.
///
/// Protobuf and not just JSON because every OpenTelemetry SDK exporter defaults
/// to protobuf. A collector that only spoke JSON would reject the standard
/// configuration and report it as a parse error, which reads as "your
/// instrumentation is broken" to somebody whose instrumentation is fine.
///
/// Field numbers below come from the OTLP v1 protos (opentelemetry/proto/
/// trace/v1/trace.proto and logs/v1/logs.proto). They are part of the wire
/// contract and do not change between releases -- that is what field numbers
/// are for -- so pinning them here is stable in a way that pinning a generated
/// package version would not be.
/// </summary>
internal static class OtlpProtobufDecoder
{
    /// <summary>ExportTraceServiceRequest { repeated ResourceSpans resource_spans = 1 }</summary>
    public static List<OtlpSpan> DecodeTraces(ReadOnlySpan<byte> body)
    {
        var spans = new List<OtlpSpan>();
        var reader = new ProtoReader(body);

        while (reader.TryReadTag(out int field, out WireType wire))
        {
            if (field == 1 && wire == WireType.LengthDelimited)
            {
                ReadResourceSpans(reader.ReadLengthDelimited(), spans);
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return spans;
    }

    /// <summary>ExportLogsServiceRequest { repeated ResourceLogs resource_logs = 1 }</summary>
    public static List<OtlpLog> DecodeLogs(ReadOnlySpan<byte> body)
    {
        var logs = new List<OtlpLog>();
        var reader = new ProtoReader(body);

        while (reader.TryReadTag(out int field, out WireType wire))
        {
            if (field == 1 && wire == WireType.LengthDelimited)
            {
                ReadResourceLogs(reader.ReadLengthDelimited(), logs);
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return logs;
    }

    /// <summary>
    /// How many metric data points arrived. The cloud intake takes logs, spans
    /// and flows; counting what is dropped is what turns "my metrics vanished"
    /// into a number on the collector's health.
    /// </summary>
    public static int CountMetricPoints(ReadOnlySpan<byte> body)
    {
        int count = 0;
        var reader = new ProtoReader(body);

        // ExportMetricsServiceRequest { repeated ResourceMetrics resource_metrics = 1 }
        while (reader.TryReadTag(out int field, out WireType wire))
        {
            if (field == 1 && wire == WireType.LengthDelimited)
            {
                count += CountInResourceMetrics(reader.ReadLengthDelimited());
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return count;
    }

    private static int CountInResourceMetrics(ReadOnlySpan<byte> body)
    {
        int count = 0;
        var reader = new ProtoReader(body);

        // ResourceMetrics { Resource resource = 1; repeated ScopeMetrics scope_metrics = 2 }
        while (reader.TryReadTag(out int field, out WireType wire))
        {
            if (field == 2 && wire == WireType.LengthDelimited)
            {
                var scope = new ProtoReader(reader.ReadLengthDelimited());

                // ScopeMetrics { scope = 1; repeated Metric metrics = 2 }
                while (scope.TryReadTag(out int scopeField, out WireType scopeWire))
                {
                    if (scopeField == 2 && scopeWire == WireType.LengthDelimited)
                    {
                        scope.ReadLengthDelimited();
                        count++;
                    }
                    else
                    {
                        scope.Skip(scopeWire);
                    }
                }
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return count;
    }

    // --- traces ---------------------------------------------------------------

    /// <summary>ResourceSpans { Resource resource = 1; repeated ScopeSpans scope_spans = 2 }</summary>
    private static void ReadResourceSpans(ReadOnlySpan<byte> body, List<OtlpSpan> into)
    {
        var resource = new Dictionary<string, object?>();
        var scopes = new List<byte[]>();
        var reader = new ProtoReader(body);

        while (reader.TryReadTag(out int field, out WireType wire))
        {
            if (wire != WireType.LengthDelimited)
            {
                reader.Skip(wire);
                continue;
            }

            switch (field)
            {
                case 1:
                    ReadAttributesInto(reader.ReadLengthDelimited(), resource, attributesField: 1);
                    break;
                case 2:
                    // Copied out: a ref struct cannot be held across the second
                    // pass, and the resource may arrive after the scopes.
                    scopes.Add(reader.ReadLengthDelimited().ToArray());
                    break;
                default:
                    reader.ReadLengthDelimited();
                    break;
            }
        }

        foreach (byte[] scope in scopes)
        {
            ReadScopeSpans(scope, resource, into);
        }
    }

    /// <summary>ScopeSpans { InstrumentationScope scope = 1; repeated Span spans = 2 }</summary>
    private static void ReadScopeSpans(
        ReadOnlySpan<byte> body, Dictionary<string, object?> resource, List<OtlpSpan> into)
    {
        var reader = new ProtoReader(body);

        while (reader.TryReadTag(out int field, out WireType wire))
        {
            if (field == 2 && wire == WireType.LengthDelimited)
            {
                into.Add(ReadSpan(reader.ReadLengthDelimited(), resource));
            }
            else
            {
                reader.Skip(wire);
            }
        }
    }

    private static OtlpSpan ReadSpan(ReadOnlySpan<byte> body, Dictionary<string, object?> resource)
    {
        string traceId = "";
        string spanId = "";
        string? parentSpanId = null;
        string name = "";
        int kind = 0;
        ulong startNano = 0;
        ulong endNano = 0;
        int statusCode = 0;
        var attributes = new Dictionary<string, object?>(resource);

        var reader = new ProtoReader(body);

        while (reader.TryReadTag(out int field, out WireType wire))
        {
            switch (field, wire)
            {
                case (1, WireType.LengthDelimited):
                    traceId = Convert.ToHexString(reader.ReadLengthDelimited()).ToLowerInvariant();
                    break;
                case (2, WireType.LengthDelimited):
                    spanId = Convert.ToHexString(reader.ReadLengthDelimited()).ToLowerInvariant();
                    break;
                case (4, WireType.LengthDelimited):
                    string parent = Convert.ToHexString(reader.ReadLengthDelimited()).ToLowerInvariant();
                    parentSpanId = parent.Length == 0 ? null : parent;
                    break;
                case (5, WireType.LengthDelimited):
                    name = reader.ReadString();
                    break;
                case (6, WireType.Varint):
                    kind = (int)reader.ReadVarint();
                    break;
                case (7, WireType.Fixed64):
                    startNano = reader.ReadFixed64();
                    break;
                case (8, WireType.Fixed64):
                    endNano = reader.ReadFixed64();
                    break;
                case (9, WireType.LengthDelimited):
                    ReadKeyValue(reader.ReadLengthDelimited(), attributes);
                    break;
                case (15, WireType.LengthDelimited):
                    statusCode = ReadStatusCode(reader.ReadLengthDelimited());
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new OtlpSpan
        {
            TraceId = traceId,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            Name = name,
            Kind = OtlpNames.SpanKind(kind),
            StartUnixNano = startNano,
            EndUnixNano = endNano,
            Status = OtlpNames.StatusCode(statusCode),
            Attributes = attributes,
        };
    }

    /// <summary>Status { string message = 2; StatusCode code = 3 }</summary>
    private static int ReadStatusCode(ReadOnlySpan<byte> body)
    {
        var reader = new ProtoReader(body);
        int code = 0;

        while (reader.TryReadTag(out int field, out WireType wire))
        {
            if (field == 3 && wire == WireType.Varint)
            {
                code = (int)reader.ReadVarint();
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return code;
    }

    // --- logs -----------------------------------------------------------------

    private static void ReadResourceLogs(ReadOnlySpan<byte> body, List<OtlpLog> into)
    {
        var resource = new Dictionary<string, object?>();
        var scopes = new List<byte[]>();
        var reader = new ProtoReader(body);

        while (reader.TryReadTag(out int field, out WireType wire))
        {
            if (wire != WireType.LengthDelimited)
            {
                reader.Skip(wire);
                continue;
            }

            switch (field)
            {
                case 1:
                    ReadAttributesInto(reader.ReadLengthDelimited(), resource, attributesField: 1);
                    break;
                case 2:
                    scopes.Add(reader.ReadLengthDelimited().ToArray());
                    break;
                default:
                    reader.ReadLengthDelimited();
                    break;
            }
        }

        foreach (byte[] scope in scopes)
        {
            var scopeReader = new ProtoReader(scope);

            // ScopeLogs { scope = 1; repeated LogRecord log_records = 2 }
            while (scopeReader.TryReadTag(out int field, out WireType wire))
            {
                if (field == 2 && wire == WireType.LengthDelimited)
                {
                    into.Add(ReadLogRecord(scopeReader.ReadLengthDelimited(), resource));
                }
                else
                {
                    scopeReader.Skip(wire);
                }
            }
        }
    }

    private static OtlpLog ReadLogRecord(ReadOnlySpan<byte> body, Dictionary<string, object?> resource)
    {
        ulong timeNano = 0;
        ulong observedNano = 0;
        int severityNumber = 0;
        string? severityText = null;
        string message = "";
        string? traceId = null;
        string? spanId = null;
        var attributes = new Dictionary<string, object?>(resource);

        var reader = new ProtoReader(body);

        while (reader.TryReadTag(out int field, out WireType wire))
        {
            switch (field, wire)
            {
                case (1, WireType.Fixed64):
                    timeNano = reader.ReadFixed64();
                    break;
                case (2, WireType.Varint):
                    severityNumber = (int)reader.ReadVarint();
                    break;
                case (3, WireType.LengthDelimited):
                    severityText = reader.ReadString();
                    break;
                case (5, WireType.LengthDelimited):
                    message = AnyValueToString(reader.ReadLengthDelimited());
                    break;
                case (6, WireType.LengthDelimited):
                    ReadKeyValue(reader.ReadLengthDelimited(), attributes);
                    break;
                case (9, WireType.LengthDelimited):
                    traceId = Convert.ToHexString(reader.ReadLengthDelimited()).ToLowerInvariant();
                    break;
                case (10, WireType.LengthDelimited):
                    spanId = Convert.ToHexString(reader.ReadLengthDelimited()).ToLowerInvariant();
                    break;
                case (11, WireType.Fixed64):
                    observedNano = reader.ReadFixed64();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new OtlpLog
        {
            // An SDK that only sets observed_time still gets a timestamp, which
            // is the difference between a log landing on today's dashboard and
            // landing at the epoch.
            TimeUnixNano = timeNano != 0 ? timeNano : observedNano,
            Level = OtlpNames.Severity(severityNumber, severityText),
            Message = message,
            TraceId = string.IsNullOrEmpty(traceId) ? null : traceId,
            SpanId = string.IsNullOrEmpty(spanId) ? null : spanId,
            Attributes = attributes,
        };
    }

    // --- shared ---------------------------------------------------------------

    /// <summary>Resource { repeated KeyValue attributes = 1 }</summary>
    private static void ReadAttributesInto(
        ReadOnlySpan<byte> body, Dictionary<string, object?> into, int attributesField)
    {
        var reader = new ProtoReader(body);

        while (reader.TryReadTag(out int field, out WireType wire))
        {
            if (field == attributesField && wire == WireType.LengthDelimited)
            {
                ReadKeyValue(reader.ReadLengthDelimited(), into);
            }
            else
            {
                reader.Skip(wire);
            }
        }
    }

    /// <summary>KeyValue { string key = 1; AnyValue value = 2 }</summary>
    private static void ReadKeyValue(ReadOnlySpan<byte> body, Dictionary<string, object?> into)
    {
        string? key = null;
        object? value = null;
        var reader = new ProtoReader(body);

        while (reader.TryReadTag(out int field, out WireType wire))
        {
            switch (field, wire)
            {
                case (1, WireType.LengthDelimited):
                    key = reader.ReadString();
                    break;
                case (2, WireType.LengthDelimited):
                    value = ReadAnyValue(reader.ReadLengthDelimited());
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (!string.IsNullOrEmpty(key))
        {
            into[key] = value;
        }
    }

    /// <summary>
    /// AnyValue { string=1, bool=2, int64=3, double=4, array=5, kvlist=6, bytes=7 }
    ///
    /// Arrays and maps are rendered as text rather than nested structures: the
    /// far end stores attributes as a flat map and a nested value there would
    /// serialise as something no query can filter on.
    /// </summary>
    private static object? ReadAnyValue(ReadOnlySpan<byte> body)
    {
        var reader = new ProtoReader(body);

        while (reader.TryReadTag(out int field, out WireType wire))
        {
            switch (field, wire)
            {
                case (1, WireType.LengthDelimited):
                    return reader.ReadString();
                case (2, WireType.Varint):
                    return reader.ReadVarint() != 0;
                case (3, WireType.Varint):
                    return (long)reader.ReadVarint();
                case (4, WireType.Fixed64):
                    return reader.ReadDouble();
                case (7, WireType.LengthDelimited):
                    return Convert.ToHexString(reader.ReadLengthDelimited()).ToLowerInvariant();
                case (5, WireType.LengthDelimited):
                case (6, WireType.LengthDelimited):
                    return "(structured)";
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return null;
    }

    private static string AnyValueToString(ReadOnlySpan<byte> body) =>
        ReadAnyValue(body) switch
        {
            null => "",
            string s => s,
            bool b => b ? "true" : "false",
            var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };
}
