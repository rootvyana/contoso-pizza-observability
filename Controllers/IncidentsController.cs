using Microsoft.AspNetCore.Mvc;

namespace ContosoPizza.Controllers;

/// <summary>
/// Endpoints that fail on purpose.
///
/// A monitoring stack is only worth anything on the bad path, and a service whose
/// every response is 200 proves nothing about it. These three produce the failures
/// worth being able to tell apart from a dashboard: a downstream that will not
/// answer, a bug in this process, and a route that is merely slow.
///
/// Each one logs through ILogger, which the OpenTelemetry logging provider exports
/// with the active trace and span id attached. That is what makes the log line
/// findable from the trace rather than something you go and grep for separately.
/// </summary>
[ApiController]
[Route("api/incidents")]
public class IncidentsController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IncidentsController> _logger;

    public IncidentsController(
        IHttpClientFactory httpClientFactory, ILogger<IncidentsController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// A downstream call that cannot succeed.
    ///
    /// Deliberately a real outbound request rather than a thrown exception: the
    /// HttpClient instrumentation records it as a child span, so the trace has a
    /// shape — a parent that took 500ms and a child that is all of it — instead of
    /// the single bar every other route in this app produces. Port 9 is the discard
    /// service; nothing listens, so the connection is refused immediately and
    /// predictably rather than depending on a timeout.
    /// </summary>
    [HttpGet("upstream")]
    public async Task<IActionResult> UpstreamFailure()
    {
        const string upstream = "http://127.0.0.1:9/inventory/reserve";
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(2);

        _logger.LogInformation("Reserving inventory via {Upstream}", upstream);

        try
        {
            using var response = await client.GetAsync(upstream).ConfigureAwait(false);
            return Ok(new { reserved = true });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Logged with the exception, so the log record carries the type and stack
            // and not just a sentence somebody wrote about it.
            _logger.LogError(ex,
                "Inventory service did not answer at {Upstream}; order cannot be reserved",
                upstream);

            // 502, not 500: this process is fine and something it depends on is not.
            // A dashboard that cannot tell those apart sends the wrong person.
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "upstream_unavailable",
                upstream,
                detail = ex.Message,
            });
        }
    }

    /// <summary>An unhandled fault in this process. The one that is our own bug.</summary>
    [HttpGet("crash")]
    public IActionResult Crash()
    {
        _logger.LogWarning("Pricing lookup entered the branch that was never finished");
        throw new InvalidOperationException(
            "Pricing table has no entry for size 'family' — this is a bug in ContosoPizza, "
          + "not a bad request.");
    }

    /// <summary>
    /// Slow, and successful. Worth having because it is the case a status-code view
    /// cannot see at all: every response here is a 200 and the route is still the
    /// worst thing on the page.
    /// </summary>
    [HttpGet("slow")]
    public async Task<IActionResult> Slow([FromQuery] int ms = 1500)
    {
        int delay = Math.Clamp(ms, 0, 10_000);
        _logger.LogInformation("Deliberately sleeping {Delay}ms", delay);
        await Task.Delay(delay).ConfigureAwait(false);
        return Ok(new { sleptMs = delay });
    }
}
