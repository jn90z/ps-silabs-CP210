# Plex MES Automation & Orchestration Integration

This project exposes a small HTTP API intended to let Plex MES Automation & Orchestration control the SPPS2001D power supply through the same `PowerSupplyController` used by the Windows WPF application.

## Target Production Flow

```text
Plex MES job/workcenter context
        |
        | PartNumber
        v
Plex MES Automation & Orchestration
        |
        | HTTP/JSON
        v
SPPS2001D Pro API on EOL PC
        |
        v
PowerSupplyController
        |
        v
SerialService -> CP210x -> Power Supply
```

The power supply is treated as a job-level resource. Loading a job applies the recipe for the unique `PartNumber`, enables output, and leaves the output on while that job remains active. Unloading or changing the job turns output off as part of the controller lifecycle.

## API Configuration

The application reads three environment variables at startup.

| Variable | Purpose | Default |
| --- | --- | --- |
| `SPPS_RECIPE_FILE` | Excel recipe workbook path, including UNC paths | `PowerSupplyRecipes.xlsx` beside the application |
| `SPPS_API_URL` | HTTP listener URL | `http://127.0.0.1:5187` |
| `SPPS_API_KEY` | Shared API key for authenticated requests | unset |

### Same-PC Plex/AO runtime

If the AO component making the HTTP request runs on the same EOL PC, the default loopback binding can be used:

```text
SPPS_API_URL=http://127.0.0.1:5187
```

An API key is optional for loopback-only operation, though it may still be configured.

### Remote Plex/AO runtime

If AO reaches the EOL PC from another machine, bind to a network-accessible interface and configure an API key.

Example:

```text
SPPS_API_URL=http://0.0.0.0:5187
SPPS_API_KEY=<long-random-secret>
```

The application intentionally refuses to start the API on a non-loopback address unless `SPPS_API_KEY` is configured.

For production, also restrict the Windows Firewall rule so TCP port 5187 is reachable only from the expected AO/edge host or subnet.

## Authentication

`GET /api/health` is intentionally unauthenticated for basic availability monitoring.

When `SPPS_API_KEY` is configured, every other `/api/*` request must include:

```text
X-API-Key: <configured key>
```

Do not store the key in source control. Store it in the workstation/AO secret or configuration mechanism used by your plant.

## Endpoints

### Health

```http
GET /api/health
```

Example response:

```json
{
  "status": "ok",
  "service": "SPPS2001D_Pro",
  "apiVersion": 1,
  "authenticationEnabled": true
}
```

### Status

```http
GET /api/status
X-API-Key: <key>
```

Example response:

```json
{
  "connected": true,
  "outputOn": true,
  "productionState": "JobActive",
  "jobActive": true,
  "partNumber": "CL323",
  "voltageSetpoint": 24.0,
  "currentSetpoint": 0.5,
  "voltageActual": 23.98,
  "currentActual": 0.487,
  "powerActual": 11.67826,
  "mode": "CV",
  "serialStatus": "Connected"
}
```

### Load job

```http
POST /api/job/load
Content-Type: application/json
X-API-Key: <key>

{
  "partNumber": "CL323"
}
```

The unique `PartNumber` is the recipe key. Revision is intentionally not part of the lookup.

Successful load returns HTTP 200 and the current status. A controller/job conflict returns HTTP 409. Recipe/configuration errors return a 5xx response with problem details.

### Unload job

```http
POST /api/job/unload
X-API-Key: <key>
```

This ends the active job lifecycle and turns the output off.

### Emergency/operator-requested output off

```http
POST /api/output/off
X-API-Key: <key>
```

This gives AO a direct safe-state action without exposing a general-purpose output-on endpoint.

## Recommended AO Workflow

Use the AO job/workcenter context as the source of the active Plex part number.

```text
JOB LOADED / PART CHANGED
        |
        v
POST /api/job/load { partNumber }
        |
        +-- 200 -> poll /api/status -> display READY FOR BOARDS
        |
        +-- non-200 -> display PSU FAULT / RECIPE ERROR and do not proceed

SAME JOB REMAINS ACTIVE
        |
        v
No repeated load command required
        |
        v
Poll /api/status for operator display/health

JOB UNLOADED / COMPLETE
        |
        v
POST /api/job/unload
```

The operator panel should primarily display:

- Active part number
- Recipe voltage and current
- Actual voltage and current
- Calculated power
- CV/CC state
- PSU connection state
- Output state
- Production state
- A large `READY FOR BOARDS` indication only when the expected job is active and the PSU is connected with output enabled

## Suggested READY Logic

AO should consider the station ready only when all of the following are true:

```text
connected == true
jobActive == true
outputOn == true
productionState == "JobActive"
partNumber == Plex active PartNumber
```

If any of those conditions are false, the operator interface should show a non-ready state and the specific reason when practical.

## Recipe Workbook

Current workbook columns are:

```text
PartNumber | Voltage | Current | MaxVoltage | MaxCurrent
```

Required columns:

- `PartNumber`
- `Voltage`
- `Current`

Optional columns:

- `MaxVoltage`
- `MaxCurrent`

`PartNumber` values must be unique. Duplicate part numbers are treated as configuration errors rather than guessed or resolved automatically.

## Initial AO Commissioning Test

Before connecting a production job event, verify the bridge in this order:

1. Start the Windows application.
2. Call `GET /api/health` from the AO/edge host.
3. Call `GET /api/status` with `X-API-Key` if authentication is enabled.
4. Confirm the expected CP210x COM port is connected.
5. Use a known test part number and call `POST /api/job/load`.
6. Verify the PSU receives the expected recipe and the API reports `JobActive` and `outputOn=true`.
7. Call `POST /api/job/unload` and verify output turns off.
8. Only after the above passes, connect the AO flow to the real Plex job/workcenter event.

## Remaining Production Validation

The HTTP bridge is intentionally narrow and suitable for initial AO integration. Before production release, physical hardware testing should confirm command/readback behavior and the desired setpoint-verification sequence before output is enabled.

The API should not be exposed broadly to the plant LAN. Keep it on loopback where possible; otherwise use the API key plus a restrictive host firewall rule.
