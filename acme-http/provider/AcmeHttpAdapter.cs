using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BeaconTower.ProviderSdk.Adapters;

namespace AcmeHttp;

/// <summary>
/// A provider that speaks plain HTTP in both directions.
///
/// Devices POST telemetry and reported properties IN. The adapter pushes
/// commands and desired properties BACK OUT, to a callback URL the device
/// registered. That second half is what a poll-only HTTP provider leaves out,
/// and leaving it out means <c>SupportsCommands</c> is false and every command
/// the platform sends comes back "not supported".
///
/// The SDK owns NATS, provisioning, the twin and the telemetry pipeline. This
/// class owns the wire: what arrives goes in through <c>Telemetry</c>, and what
/// has to reach a device goes out through an HTTP call made here.
/// </summary>
public sealed class AcmeHttpAdapter(
    AdapterContext context,
    IHttpClientFactory http,
    ILogger<AcmeHttpAdapter> logger) : BaseAdapter(context)
{
    /// <summary>
    /// What this adapter needs to know about a device: the key it authenticates
    /// with, where to call it back, and when it last spoke.
    /// </summary>
    private sealed record Device(string Key, string? Callback, DateTimeOffset LastSeen);

    /// <summary>
    /// In memory on purpose - it is the smallest thing that shows the shape.
    /// A restart loses the callbacks, so devices re-register when they boot,
    /// which is why the simulator registers before it sends anything. A real
    /// provider keeps this in its own database, which the SDK already gives it.
    /// </summary>
    private readonly ConcurrentDictionary<string, Device> _devices = new();

    /// <summary>
    /// What the platform may ask of this transport. SupportsCommands is the
    /// interesting one: it is true only because of the callback below.
    /// </summary>
    public override AdapterCapabilities Capabilities => new()
    {
        SupportsRetain = false,
        RetainSurvivesRestart = false,
        SupportsCommands = true,
    };

    /// <summary>
    /// Nothing to open. The listener is the host's own Kestrel, wired up in
    /// Program.cs; a protocol that dials out to a broker would connect here.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("acme-http adapter started");
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("acme-http adapter stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The platform provisions a device and hands us the key it issued. The
    /// device never invents its own credential - it is given one, and this is
    /// where the transport learns it.
    ///
    /// This runs inside the provisioning transaction, so returning Failed rolls
    /// the whole thing back rather than leaving a half-provisioned device.
    /// </summary>
    public override Task<ProvisionResult> OnProvisionAsync(
        ProvisionContext context, CancellationToken cancellationToken)
    {
        _devices.AddOrUpdate(
            context.DeviceId,
            _ => new Device(context.PrimaryKey ?? "", null, DateTimeOffset.UtcNow),
            (_, existing) => existing with { Key = context.PrimaryKey ?? existing.Key });
        logger.LogInformation("provisioned {DeviceId}", context.DeviceId);
        return Task.FromResult(ProvisionResult.Ok());
    }

    /// <summary>Removes what OnProvisionAsync created.</summary>
    public override Task<ProvisionResult> OnUnprovisionAsync(
        string deviceId, CancellationToken cancellationToken)
    {
        _devices.TryRemove(deviceId, out _);
        logger.LogInformation("unprovisioned {DeviceId}", deviceId);
        return Task.FromResult(ProvisionResult.Ok());
    }

    // ---- device -> platform -------------------------------------------------

    /// <summary>
    /// Records where the platform can reach this device, and returns its key.
    ///
    /// A device that was never provisioned gets a freshly minted key here, so
    /// the example runs without standing up the whole onboarding chain first.
    /// A provider facing real hardware should refuse an unknown device and let
    /// provisioning issue the credential instead.
    /// </summary>
    public string Register(string deviceId, string callback)
    {
        var device = _devices.AddOrUpdate(
            deviceId,
            _ => new Device(Guid.NewGuid().ToString("N"), callback, DateTimeOffset.UtcNow),
            (_, existing) => existing with { Callback = callback, LastSeen = DateTimeOffset.UtcNow });
        logger.LogInformation("{DeviceId} registered callback {Callback}", deviceId, callback);
        return device.Key;
    }

    /// <summary>
    /// Checks the bearer token a device sent, and treats a valid call as a sign
    /// of life - it is what IsDeviceConnected reads.
    /// </summary>
    public bool Authenticate(string deviceId, string? bearer)
    {
        if (string.IsNullOrEmpty(bearer) || !_devices.TryGetValue(deviceId, out var device))
        {
            return false;
        }

        if (!string.Equals(device.Key, bearer, StringComparison.Ordinal))
        {
            return false;
        }

        _devices[deviceId] = device with { LastSeen = DateTimeOffset.UtcNow };
        return true;
    }

    /// <summary>
    /// Hands an inbound payload to the SDK, which batches, enriches and forwards
    /// it. Set reportedProperties for a twin update rather than a reading.
    ///
    /// Awaiting the returned task lets the HTTP handler propagate the pipeline's
    /// backpressure to the device - it slows the caller down instead of quietly
    /// dropping messages.
    /// </summary>
    public ValueTask IngestAsync(string deviceId, byte[] payload, bool reportedProperties, CancellationToken ct)
        => Telemetry.SendAsync(deviceId, new TelemetryMessage
        {
            Payload = payload,
            IsReportedProperties = reportedProperties,
        }, ct);

    // ---- platform -> device -------------------------------------------------

    /// <summary>
    /// Delivers a command by calling the device's callback and waiting for its
    /// verdict.
    ///
    /// The three outcomes are worth keeping apart: no callback or an
    /// unreachable device is a TRANSPORT error and may be retried; a device
    /// that ran the command and refused is a DEVICE error and must not be,
    /// because retrying would just ask it to refuse again.
    /// </summary>
    public override async Task<CommandResult> SendCommandAsync(
        AdapterCommand command, CancellationToken cancellationToken)
    {
        var correlationId = command.CorrelationId ?? string.Empty;
        if (!_devices.TryGetValue(command.DeviceId, out var device) || device.Callback is null)
        {
            return Fail(correlationId, CommandStatus.TransportError,
                "device has not registered a callback", retryable: true);
        }

        try
        {
            using var response = await http.CreateClient("device").PostAsJsonAsync(
                device.Callback.TrimEnd('/') + "/commands",
                new { correlationId, name = command.CommandName, payload = command.Payload },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Fail(correlationId, CommandStatus.TransportError,
                    $"device answered {(int)response.StatusCode}", retryable: true);
            }

            var body = await response.Content.ReadFromJsonAsync<DeviceCommandResult>(cancellationToken);
            if (string.Equals(body?.Status, nameof(CommandStatus.Ok), StringComparison.OrdinalIgnoreCase))
            {
                return new CommandResult
                {
                    CorrelationId = correlationId,
                    Status = CommandStatus.Ok,
                    StatusCode = 200,
                    Retryable = false,
                };
            }

            return Fail(correlationId, CommandStatus.DeviceError,
                body?.ErrorMessage ?? "device reported failure", retryable: false, statusCode: 400);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Fail(correlationId, CommandStatus.TransportError, ex.Message, retryable: true);
        }
    }

    /// <summary>
    /// Pushes desired properties down to the device. The device applies what it
    /// can and reports back what it actually applied; that round trip, not this
    /// call, is how the platform knows a setting took.
    /// </summary>
    public override async Task SendDesiredPropertiesAsync(
        string deviceId, JsonObject properties, CancellationToken cancellationToken)
    {
        if (!_devices.TryGetValue(deviceId, out var device) || device.Callback is null)
        {
            logger.LogWarning("no callback for {DeviceId}; desired properties not delivered", deviceId);
            return;
        }

        using var content = new StringContent(
            properties.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.CreateClient("device")
            .PostAsync(device.Callback.TrimEnd('/') + "/desired", content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("{DeviceId} answered {Status} to desired properties",
                deviceId, (int)response.StatusCode);
        }
    }

    /// <summary>
    /// HTTP has no socket to consult, so "connected" is the last time the
    /// device spoke. A protocol with a real session should answer from that.
    /// </summary>
    public override bool IsDeviceConnected(string deviceId)
        => _devices.TryGetValue(deviceId, out var d)
           && d.Callback is not null
           && DateTimeOffset.UtcNow - d.LastSeen < TimeSpan.FromMinutes(5);

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Builds a failed result in the shape the SDK expects: ErrorCode must equal
    /// the Status name, and StatusCode stays null for anything that is not Ok or
    /// DeviceError. The SDK treats that as contract, not as advice.
    /// </summary>
    private static CommandResult Fail(
        string correlationId, CommandStatus status, string message, bool retryable, int? statusCode = null)
        => new()
        {
            CorrelationId = correlationId,
            Status = status,
            StatusCode = statusCode,
            ErrorCode = status.ToString(),
            ErrorMessage = message,
            Retryable = retryable,
        };

    /// <summary>What a device answers a command with. <c>status</c> is a
    /// CommandStatus name - "Ok" or "DeviceError" are the two a device decides.</summary>
    private sealed record DeviceCommandResult(
        [property: System.Text.Json.Serialization.JsonPropertyName("correlationId")] string? CorrelationId,
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string? Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("errorMessage")] string? ErrorMessage);
}
