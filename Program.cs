using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// IncidentsController makes a real outbound call so the failure it simulates shows up
// as a child span, rather than a single bar with nothing under it.
builder.Services.AddHttpClient();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

const string serviceName = "ContosoPizza";

// Request-level telemetry. The eBPF sockops program sees TCP flows but cannot
// see inside the process, so routes, status codes and latency come from here.
// client.port is tagged on every span deliberately: it is the join key back
// into the eBPF flow_map, whose key includes the remote port.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: serviceName,
        serviceVersion: "1.0.0",
        // Entity-level join key. The agent reports flows with process.pid;
        // this is what ties those back to a named service instance and is the
        // join that keeps working when ephemeral ports get reused.
        serviceInstanceId: $"{Environment.MachineName}:{Environment.ProcessId}"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(options =>
        {
            options.RecordException = true;
            options.EnrichWithHttpRequest = (activity, request) =>
            {
                var connection = request.HttpContext.Connection;
                activity.SetTag("client.address", connection.RemoteIpAddress?.ToString());
                activity.SetTag("client.port", connection.RemotePort);
            };
        })
        .AddHttpClientInstrumentation()
        // Was AddConsoleExporter, which wrote to app.log. The instrumentation
        // above is unchanged -- only the destination moved.
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter())
    ;

// Logs were the missing third signal: traces said a request failed and nothing said
// why. The provider attaches the active trace and span id to every record, which is
// what lets a log line be found from the trace instead of grepped for.
//
// On the logging builder rather than WithLogging(): the OTLP log exporter extends
// OpenTelemetryLoggerOptions, and this overload is also what picks up the resource
// configured above, so log records carry the same service.name and host.name the
// spans do. Without that they arrive attributed to nothing.
builder.Logging.AddOpenTelemetry(options =>
{
    // Otherwise the body is the message *template* -- "Reserving inventory via
    // {Upstream}" -- and the value that makes the line worth reading is only in the
    // attributes, if at all.
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
    options.ParseStateValues = true;
    options.AddOtlpExporter();
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
