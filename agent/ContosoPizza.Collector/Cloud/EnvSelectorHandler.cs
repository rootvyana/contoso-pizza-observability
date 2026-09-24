using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContosoPizza.Collector.Cloud;

/// <summary>
/// Adds the deployment's env selector to every outgoing request body.
///
/// app.loveheartbeat.com is one hostname in front of three isolated backends -- dev,
/// qa and prod -- and the router between them picks one by matching an opaque token.
/// Without it nothing reaches an application at all:
///
///     {"detail": "The env selector is required in the JSON payload"}
///
/// which looks exactly like a broken collector and is not one. A browser gets the
/// token from runtime-config.js, written at deploy time; a collector has no page load
/// to get it from, so it is configuration.
///
/// Done as a handler rather than a field on each request record because `env` is not
/// part of the API's contract -- shared/core/collectors.py has never heard of it, and
/// putting it in the wire records would imply it had. It belongs to the edge in front,
/// so it is added at the edge of this process, once, where a route added later cannot
/// forget it.
///
/// The body, not the query string: that is the convention the frontend's api-client
/// already follows for writes, and it keeps the selector out of access logs and proxy
/// history. The router rejects it in the query for a POST anyway.
/// </summary>
public sealed class EnvSelectorHandler : DelegatingHandler
{
    private readonly string _envToken;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public EnvSelectorHandler(string envToken) => _envToken = envToken;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_envToken.Length > 0 && request.Content is not null)
        {
            string? mediaType = request.Content.Headers.ContentType?.MediaType;

            if (mediaType is "application/json")
            {
                string body = await request.Content
                    .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (Rewrite(body) is { } rewritten)
                {
                    request.Content = new StringContent(rewritten, Encoding.UTF8)
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
                    };
                }
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The body with `env` added, or null to send it untouched.
    ///
    /// Null rather than throwing on anything unexpected: a selector that could not be
    /// added produces one clear rejection from the router, while a handler that throws
    /// turns every request into a transport error and tells the operator nothing about
    /// which half is wrong.
    /// </summary>
    private string? Rewrite(string body)
    {
        try
        {
            JsonNode? node = string.IsNullOrWhiteSpace(body)
                ? new JsonObject()
                : JsonNode.Parse(body);

            if (node is not JsonObject obj)
            {
                return null;
            }

            obj["env"] = _envToken;
            return obj.ToJsonString(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
