using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContosoPizza.Shared.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContosoPizza.Collector.Cloud;

/// <summary>
/// The collector's authentication to loveheartbeat.com, designed so that it
/// never expires from the operator's point of view.
///
/// Three properties make that true, and all three are needed:
///
///   1. The durable credential, minted once at enrollment, has no expiry. It is
///      the only thing a human ever has to provide, and only on the first run.
///      Nothing revokes it but an explicit revoke from the dashboard.
///
///   2. Requests carry a short-lived session instead of that credential, and
///      the session is renewed *proactively* -- before it expires, on the
///      server's own renew_after hint, minus a skew for clock drift. A
///      collector that is running therefore never presents an expired session.
///
///   3. If a session is somehow refused anyway -- a restarted backend, a clock
///      that jumped, a race at the boundary -- Invalidate() plus the durable
///      credential mints a new one and the caller retries. There is no state
///      the collector can reach where a human has to intervene.
///
/// Renewal is single-flight. The spool pump and the heartbeat both call
/// GetSessionAsync on their own schedules, and without the gate a session
/// expiring at 3am would have two threads racing to replace it, each
/// invalidating the other's brand-new token.
/// </summary>
public sealed class CloudSession
{
    private readonly HttpClient _http;
    private readonly CredentialStore _store;
    private readonly CloudOptions _options;
    private readonly CollectorState _state;
    private readonly ILogger<CloudSession> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private CollectorCredentials? _credentials;
    private string? _sessionToken;
    private DateTimeOffset _renewAt = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public CloudSession(
        HttpClient http,
        CredentialStore store,
        IOptions<CloudOptions> options,
        CollectorState state,
        ILogger<CloudSession> logger)
    {
        _http = http;
        _store = store;
        _options = options.Value;
        _state = state;
        _logger = logger;
    }

    public string? CollectorId => _credentials?.CollectorId;

    public bool Enrolled => _credentials is not null;

    /// <summary>
    /// A usable session token, enrolling or renewing as required. Null means
    /// the cloud could not be reached or the collector is not enrolled yet --
    /// the caller spools and tries again rather than dropping anything.
    /// </summary>
    public async Task<string?> GetSessionAsync(CancellationToken ct)
    {
        if (_sessionToken is not null && DateTimeOffset.UtcNow < _renewAt)
        {
            return _sessionToken;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Re-check inside the gate: whoever held it may have just renewed.
            if (_sessionToken is not null && DateTimeOffset.UtcNow < _renewAt)
            {
                return _sessionToken;
            }

            _credentials ??= LoadOrConfigure();

            if (_credentials is null)
            {
                if (!await TryEnrollAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }
            }

            return await TryOpenSessionAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forget the current session after a 401. The next GetSessionAsync mints a
    /// fresh one from the durable credential, so a refused request costs one
    /// extra round trip rather than the batch.
    /// </summary>
    public void Invalidate()
    {
        _sessionToken = null;
        _renewAt = DateTimeOffset.MinValue;
        _state.CloudAuthenticated = false;
    }

    /// <summary>
    /// Attach the session to a request. Separate from GetSessionAsync so that
    /// every caller sends the same header name and none has to know it.
    /// </summary>
    public static void Authorize(HttpRequestMessage request, string sessionToken) =>
        request.Headers.TryAddWithoutValidation("X-Collector-Session", sessionToken);

    private CollectorCredentials? LoadOrConfigure()
    {
        var stored = _store.Load();
        if (stored is not null)
        {
            _logger.LogInformation(
                "Using stored credential for collector {CollectorId} (enrolled {EnrolledAt:u})",
                stored.CollectorId, stored.EnrolledAt);
            return stored;
        }

        // A deployment that provisions collectors out of band can supply both
        // values directly and skip the enrollment round trip entirely.
        if (_options.CollectorCredential is { Length: > 0 } credential &&
            _options.CollectorId is { Length: > 0 } collectorId)
        {
            var configured = new CollectorCredentials
            {
                CollectorId = collectorId,
                Credential = credential,
                EnrolledAt = DateTimeOffset.UtcNow,
                Endpoint = _options.Endpoint,
            };

            _store.Save(configured);
            _logger.LogInformation("Using the pre-issued credential for collector {Id}", collectorId);
            return configured;
        }

        return null;
    }

    private async Task<bool> TryEnrollAsync(CancellationToken ct)
    {
        string token = _options.ResolveEnrollmentToken();

        if (string.IsNullOrEmpty(token))
        {
            // Logged at warning, not error, and repeatedly: this is the normal
            // state of a freshly installed collector waiting for somebody to
            // paste the token in, not a fault.
            _logger.LogWarning(
                "Not enrolled and no enrollment token configured. Create a collector in "
              + "loveheartbeat and set CONTOSO_COLLECTOR_ENROLLMENT_TOKEN (or Cloud:EnrollmentToken). "
              + "Telemetry is being spooled to disk in the meantime.");
            return false;
        }

        _logger.LogInformation("Enrolling with {Url}", _options.EnrollUrl);

        try
        {
            using var response = await _http.PostAsJsonAsync(
                _options.EnrollUrl,
                new EnrollRequest
                {
                    Token = token,
                    Hostname = Environment.MachineName,
                    Version = CollectorVersion,
                },
                Json,
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogError(
                    "Enrollment refused ({Status}): {Body}", (int)response.StatusCode, Truncate(body));

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    _logger.LogError(
                        "An enrollment token can be used once and expires after 24 hours. "
                      + "Issue a new one from the dashboard.");
                }

                return false;
            }

            var enrolled = await response.Content
                .ReadFromJsonAsync<EnrollResponse>(Json, ct).ConfigureAwait(false);

            if (enrolled is null || string.IsNullOrEmpty(enrolled.CollectorCredential))
            {
                _logger.LogError("Enrollment response carried no credential");
                return false;
            }

            _credentials = new CollectorCredentials
            {
                CollectorId = enrolled.CollectorId,
                Credential = enrolled.CollectorCredential,
                TenantId = enrolled.TenantId,
                EnrolledAt = DateTimeOffset.UtcNow,
                Endpoint = _options.Endpoint,
            };

            // Persisted before the session is used, so a crash in the next
            // millisecond does not strand a credential that exists in the cloud
            // and nowhere else -- the enrollment token is already spent, and
            // losing it here would need a human to issue a new one.
            _store.Save(_credentials);

            AcceptSession(
                enrolled.SessionToken, enrolled.SessionExpiresAt, enrolled.RenewAfterSeconds);
            _state.Apply(enrolled.Config);
            _state.ConfigVersion = enrolled.ConfigVersion;

            _logger.LogInformation(
                "Enrolled as collector {CollectorId}. The credential is stored and does not expire.",
                enrolled.CollectorId);

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Could not reach {Url} to enroll: {Error}", _options.EnrollUrl, ex.Message);
            return false;
        }
    }

    private async Task<string?> TryOpenSessionAsync(CancellationToken ct)
    {
        if (_credentials is null)
        {
            return null;
        }

        // Enrollment already returned one; do not immediately spend a round trip
        // replacing a session that is minutes old.
        if (_sessionToken is not null && DateTimeOffset.UtcNow < _renewAt)
        {
            return _sessionToken;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.SessionUrl)
            {
                Content = JsonContent.Create(
                    new SessionRequest { CollectorId = _credentials.CollectorId }, options: Json),
            };

            request.Headers.TryAddWithoutValidation(
                "X-Collector-Credential", _credentials.Credential);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                // The one genuinely terminal case. Everything else the collector
                // recovers from on its own; a revoked credential is somebody
                // deciding it should not.
                _logger.LogError(
                    "The cloud rejected this collector's credential: {Body}. It has most likely "
                  + "been revoked in the dashboard. Register a new collector and re-enroll.",
                    Truncate(body));

                _state.CloudAuthenticated = false;
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "Could not open a session ({Status}): {Body}",
                    (int)response.StatusCode, Truncate(body));
                return null;
            }

            var session = await response.Content
                .ReadFromJsonAsync<SessionResponse>(Json, ct).ConfigureAwait(false);

            if (session is null || string.IsNullOrEmpty(session.SessionToken))
            {
                _logger.LogWarning("Session response carried no token");
                return null;
            }

            AcceptSession(session.SessionToken, session.SessionExpiresAt, session.RenewAfterSeconds);
            _state.Apply(session.Config);
            _state.ConfigVersion = session.ConfigVersion;

            return _sessionToken;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Could not reach {Url}: {Error}", _options.SessionUrl, ex.Message);
            _state.CloudAuthenticated = false;
            return null;
        }
    }

    /// <summary>
    /// Adopt a new session and decide when to replace it.
    ///
    /// The renewal moment is the earlier of the server's hint and the real
    /// expiry minus a skew, so a hint that is missing, zero, or longer than the
    /// session itself cannot produce a token the collector believes in past the
    /// point the server stops accepting it.
    /// </summary>
    private void AcceptSession(string token, DateTimeOffset? expiresAt, int renewAfterSeconds)
    {
        var now = DateTimeOffset.UtcNow;

        var byHint = renewAfterSeconds > 0
            ? now.AddSeconds(renewAfterSeconds)
            : DateTimeOffset.MaxValue;

        var byExpiry = expiresAt.HasValue
            ? expiresAt.Value - _options.SessionRenewSkew
            : now.AddMinutes(30);

        var renewAt = byHint < byExpiry ? byHint : byExpiry;

        // A skew larger than the whole session lifetime would put the renewal
        // moment in the past and turn every request into a renewal.
        if (renewAt <= now)
        {
            renewAt = now.AddMinutes(1);
        }

        _sessionToken = token;
        _renewAt = renewAt;
        _state.CloudAuthenticated = true;

        _logger.LogInformation(
            "Cloud session active. Renewing at {RenewAt:u} (expires {ExpiresAt:u}).",
            renewAt, expiresAt);
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "...";

    public const string CollectorVersion = "0.2.0";
}
