# acme-http

A worked pair: a BeaconTower provider that speaks plain HTTP, and a device
simulator that talks to it. Both are deliberately small enough to read in one
sitting.

This directory is the **master copy of the contract**. The guide that teaches it
lives in the developer docs; if the two disagree, this one is right.

```
device/     Go device simulator, standard library only
provider/   C# provider built on BeaconTower.ProviderSdk
```

## Why both halves

A device is not a client that polls. It POSTs readings up, and it **listens**,
so the platform can reach it. Without that second half a provider must answer
"not supported" to every command, and desired properties never arrive - which
is exactly what `AdapterCapabilities.SupportsCommands = false` means.

## The contract

### Device to provider

| Method | Path | Body | Success |
|---|---|---|---|
| POST | `/api/devices/{id}/register` | `{"callback":"http://host:port"}` | `200 {"key":"..."}` |
| POST | `/api/devices/{id}/telemetry` | flat JSON of readings | `202` |
| POST | `/api/devices/{id}/properties` | flat JSON of reported properties | `202` |

Everything but `register` carries `Authorization: Bearer <key>`.

Telemetry is a flat JSON object - `{"celsius": 21.5}`. The keys become the
ingress path the platform binds signals to, so they must match the telemetry
contents on the asset's model or the value is dropped without an error.

### Provider to device

The provider calls the URL the device registered.

| Method | Path | Body | Expected |
|---|---|---|---|
| POST | `{callback}/commands` | `{"correlationId","name","payload"}` | `200` + a result |
| POST | `{callback}/desired` | flat JSON of desired properties | `204` |

A command result is:

```json
{"correlationId": "...", "status": "Ok"}
{"correlationId": "...", "status": "DeviceError", "errorMessage": "unknown command: nope"}
```

`status` is a `CommandStatus` name. `Ok` and `DeviceError` are the two a device
decides; the provider supplies the transport ones itself when the device cannot
be reached. The distinction matters: a transport failure may be retried, a
device that ran the command and refused must not be.

Desired properties are applied and then **echoed back** as reported properties.
That round trip, not the `204`, is how the platform knows a setting took.

## Run it

```bash
cd device && go build -o acme-http-device .
./acme-http-device -provider http://localhost:5080 -id acme-01
```

| Flag | Default | |
|---|---|---|
| `-provider` | `http://localhost:5080` | provider base URL |
| `-id` | `acme-01` | device id |
| `-listen` | `127.0.0.1:7070` | address to serve the webhook on |
| `-interval` | `5s` | telemetry interval |
| `-count` | `0` | stop after n messages, 0 runs until interrupted |

The provider is an ordinary SDK provider: it needs NATS and a database, which
`btk3s provider dev` supplies.

## Simplifications

Kept deliberately, and wrong for real hardware:

- **An unknown device is given a key when it registers**, so the example runs
  without the onboarding chain in front of it. A real provider refuses an
  unknown device and lets provisioning issue the credential - `OnProvisionAsync`
  receives it as `ProvisionContext.PrimaryKey`.
- **Device records live in memory.** A restart loses them and devices
  re-register on boot. A real provider uses the database the SDK already gives
  it.
- **Presence is "spoke recently"** rather than a live session, because HTTP has
  no socket to consult.
