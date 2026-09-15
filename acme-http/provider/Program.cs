using AcmeHttp;
using BeaconTower.ProviderSdk.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("device");
builder.Services.AddBeaconTowerProvider<AcmeHttpAdapter>(
    builder, options => options.ProviderId = "acme-http");

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("healthy"));

var devices = app.MapGroup("/api/devices/{deviceId}");

devices.MapPost("/register", (string deviceId, RegisterRequest body, AcmeHttpAdapter adapter) =>
    string.IsNullOrWhiteSpace(body.Callback)
        ? Results.BadRequest(new { error = "callback is required" })
        : Results.Ok(new { key = adapter.Register(deviceId, body.Callback) }));

devices.MapPost("/telemetry", (string deviceId, HttpRequest request, AcmeHttpAdapter adapter)
    => Ingest(deviceId, request, adapter, reportedProperties: false));

devices.MapPost("/properties", (string deviceId, HttpRequest request, AcmeHttpAdapter adapter)
    => Ingest(deviceId, request, adapter, reportedProperties: true));

app.Run();

static async Task<IResult> Ingest(
    string deviceId, HttpRequest request, AcmeHttpAdapter adapter, bool reportedProperties)
{
    if (!adapter.Authenticate(deviceId, Bearer(request)))
    {
        return Results.Unauthorized();
    }

    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, request.HttpContext.RequestAborted);
    var payload = buffer.ToArray();
    if (payload.Length == 0)
    {
        return Results.BadRequest(new { error = "empty payload" });
    }

    await adapter.IngestAsync(deviceId, payload, reportedProperties, request.HttpContext.RequestAborted);
    return Results.Accepted();
}

static string? Bearer(HttpRequest request)
{
    var header = request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? header["Bearer ".Length..]
        : null;
}

public sealed record RegisterRequest(string Callback);
