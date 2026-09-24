using Microsoft.Extensions.Logging;

namespace ContosoPizza.Collector.Spool;

/// <summary>
/// Append-only, disk-backed queue of connection events.
///
/// The point of this is at-least-once delivery across a cloud outage. The
/// OpenTelemetry SDK's exporter retries in memory and then drops; an on-prem
/// agent that loses data whenever the internet blips is not a gateway.
///
/// Layout in &lt;state&gt;\spool:
///     current.jsonl        being written
///     &lt;ticks&gt;.seg     rolled, waiting to be sent
///
/// Events are JSON lines. Rolled segments are the unit of transmission and are
/// deleted only after the server accepts them, so a crash mid-send costs a
/// duplicate rather than a loss. Consumers must therefore tolerate duplicates.
/// </summary>
public sealed class EventSpool
{
    private const string CurrentFileName = "current.jsonl";
    private const string SegmentExtension = ".seg";

    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private int _currentLineCount;

    public EventSpool(string stateDirectory, long maxBytes, ILogger logger)
    {
        _directory = Path.Combine(stateDirectory, "spool");
        _maxBytes = maxBytes;
        _logger = logger;

        Directory.CreateDirectory(_directory);

        // A segment left behind by a crash is still valid data; roll whatever
        // was mid-write so the pump picks it up on this run.
        Roll();
    }

    private string CurrentPath => Path.Combine(_directory, CurrentFileName);

    /// <summary>Append one event. Safe to call from any thread.</summary>
    public void Append(string jsonLine)
    {
        lock (_gate)
        {
            try
            {
                File.AppendAllLines(CurrentPath, [jsonLine]);
                _currentLineCount++;
            }
            catch (IOException ex)
            {
                // Never let a spool failure take down collection.
                _logger.LogError(ex, "Failed to append to spool");
            }
        }
    }

    public int PendingInCurrent
    {
        get { lock (_gate) { return _currentLineCount; } }
    }

    /// <summary>
    /// Close the current file and make it available to the pump. No-op when
    /// nothing has been written.
    /// </summary>
    public void Roll()
    {
        lock (_gate)
        {
            string current = CurrentPath;

            if (!File.Exists(current) || new FileInfo(current).Length == 0)
            {
                _currentLineCount = 0;
                return;
            }

            string sealedName = Path.Combine(
                _directory,
                $"{DateTime.UtcNow.Ticks:D19}{SegmentExtension}");

            try
            {
                File.Move(current, sealedName);
                _currentLineCount = 0;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to roll spool segment");
            }
        }
    }

    /// <summary>Rolled segments, oldest first.</summary>
    public IReadOnlyList<string> ReadySegments()
    {
        try
        {
            return Directory.GetFiles(_directory, $"*{SegmentExtension}")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    public static string[] ReadSegment(string path) => File.ReadAllLines(path);

    /// <summary>Called only after the server has accepted the batch.</summary>
    public void Complete(string segmentPath)
    {
        try
        {
            File.Delete(segmentPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete sent segment {Segment}", segmentPath);
        }
    }

    public long TotalBytes =>
        ReadySegments().Sum(p => SafeLength(p)) + SafeLength(CurrentPath);

    /// <summary>
    /// Drop oldest segments once the spool exceeds its ceiling. An agent must
    /// never fill the disk of the machine it is monitoring, so the newest data
    /// wins and the loss is reported rather than hidden.
    /// </summary>
    public int EnforceQuota()
    {
        long total = TotalBytes;
        if (total <= _maxBytes)
        {
            return 0;
        }

        int dropped = 0;

        foreach (string segment in ReadySegments())
        {
            if (total <= _maxBytes)
            {
                break;
            }

            long size = SafeLength(segment);

            try
            {
                File.Delete(segment);
                total -= size;
                dropped++;
            }
            catch (IOException)
            {
                break;
            }
        }

        if (dropped > 0)
        {
            _logger.LogError(
                "Spool exceeded {MaxBytes} bytes; dropped {Dropped} oldest segment(s). "
              + "Telemetry has been lost.", _maxBytes, dropped);
        }

        return dropped;
    }

    private static long SafeLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
