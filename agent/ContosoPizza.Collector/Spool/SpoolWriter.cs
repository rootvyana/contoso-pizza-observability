using System.Text.Json;
using ContosoPizza.Shared.Wire;

namespace ContosoPizza.Collector.Spool;

/// <summary>
/// The one way anything enters the outbound queue.
///
/// Every receiver -- probes posting flows, applications exporting OTLP --
/// writes here and nowhere else, so there is exactly one place where the
/// durability guarantee holds and exactly one disk quota to reason about. A
/// receiver that wrote straight to the network would be a second delivery path
/// with its own retry behaviour and its own way of losing data.
/// </summary>
public sealed class SpoolWriter
{
    private readonly EventSpool _spool;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SpoolWriter(EventSpool spool) => _spool = spool;

    public void Write(string kind, Dictionary<string, object?> record) =>
        _spool.Append(JsonSerializer.Serialize(
            new SpooledRecord { Kind = kind, Record = record }, Json));

    public void WriteFlow(Dictionary<string, object?> flow) =>
        Write(SpooledRecord.KindFlow, flow);

    public void WriteSpan(Dictionary<string, object?> span) =>
        Write(SpooledRecord.KindSpan, span);

    public void WriteLog(Dictionary<string, object?> log) =>
        Write(SpooledRecord.KindLog, log);

    public static SpooledRecord? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<SpooledRecord>(line, Json);
        }
        catch (JsonException)
        {
            // A truncated last line is what a crash mid-append leaves behind.
            // Skipping it costs one record; refusing to read the segment would
            // cost every record in it, permanently.
            return null;
        }
    }
}
