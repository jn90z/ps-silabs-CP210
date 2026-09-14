# SPPS2001D Pro Power Supply Controller

Windows WPF application for controlling a KUAIQUA / SPPS2001D programmable DC power supply through its Silicon Labs CP210x USB-to-UART interface.

The application can be used as a standalone engineering/service controller and now also exposes a local REST API intended for Plex MES Automation & Orchestration production integration.

## Current Status

- Standalone WPF desktop controller: implemented
- CP210x serial communication: implemented
- Automatic voltage/current readback: implemented
- Production recipe model: implemented
- Excel recipe source: implemented
- Job load/unload lifecycle: implemented
- Local REST API for Plex AO: implemented
- API-key protection for non-loopback bindings: implemented
- Mode-aware recipe/readback verification before a job becomes active: implemented
- Plex AO integration contract: documented in `PLEX_AO_INTEGRATION.md`

Development is occurring on the `improvements` branch. `main` remains the stable baseline.

## Production Architecture

```text
Plex MES Operator Interface
        |
        v
Plex MES Automation & Orchestration
        |
        v
Local REST API on EOL PC
        |
        v
PowerSupplyController
        |
        v
SerialService
        |
        v
Windows COM Port / CP210x
        |
        v
KUAIQUA / SPPS2001D Power Supply
```

The WPF interface and Plex API share the same `PowerSupplyController` instance so only one application-level controller owns the power supply state.

## Intended Production Workflow

```text
Operator loads job in Plex
        |
        v
Plex supplies PartNumber
        |
        v
POST /api/job/load
        |
        v
Recipe lookup
        |
        v
Output OFF
        |
        v
Remote mode
        |
        v
Program voltage/current
        |
        v
Output ON
        |
        v
Fresh readback verification
        |
        +-- verification fails --> Output OFF --> Fault
        |
        v
ProductionState = JobActive
        |
        v
READY FOR BOARDS
```

The output remains on while the same production job is active. When the job changes or ends, Plex calls the unload endpoint and the output is turned off.

No board-level serial-number or pass/fail transaction is required for this power-supply workflow. The PSU is treated as a job-level production resource.

## Recipe Lookup

`PartNumber` is the unique recipe key. Revision is intentionally not used.

The Excel recipe provider expects the following columns:

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

Duplicate part numbers are treated as a configuration error.

Example:

```text
PartNumber   Voltage   Current   MaxVoltage   MaxCurrent
CL323        24.000    0.500     26.000       0.550
CL397        48.000    0.350     50.000       0.400
CL536A       12.000    0.250     13.000       0.300
```

## Recipe / Readback Verification

A production job is not marked `JobActive` simply because the voltage/current commands were transmitted.

After programming the recipe, the controller:

1. Enables the output.
2. Requests fresh current and voltage readback frames.
3. Requires both readbacks to be newer than the values present before the verification request.
4. Uses the reported operating mode to decide which programmed limit should match the live output.
5. Marks the job active only after verification passes.

Verification is mode-aware:

- In `CV` mode, measured voltage must match the recipe voltage within tolerance.
- In `CC` mode, measured current must match the recipe current within tolerance.

Current implementation uses four attempts with a short delay between attempts. The tolerance is 2%, with minimum floors of 0.10 V and 0.010 A.

If verification fails:

```text
Output OFF
ActiveRecipe cleared
ProductionState = Fault
READY FOR BOARDS = false
```

This approach is necessary because the current PSU protocol implementation exposes live voltage/current readback but does not provide a separate confirmed programmed-setpoint register.

Physical hardware testing is required to confirm that the PSU's CV/CC reporting and startup settling behavior are consistent with these verification limits for all production loads.

## Plex REST API

Default API URL:

```text
http://127.0.0.1:5187
```

### Health

```http
GET /api/health
```

Used for service-health monitoring.

### Status

```http
GET /api/status
```

Returns data including:

- connection state
- output state
- production state
- active part number
- recipe voltage/current
- measured voltage/current
- calculated power
- CV/CC mode
- serial status

### Load Job

```http
POST /api/job/load
Content-Type: application/json

{
  "partNumber": "CL323"
}
```

### Unload Job

```http
POST /api/job/unload
```

### Emergency / explicit output-off command

```http
POST /api/output/off
```

## READY FOR BOARDS Logic

The Plex operator interface should only show `READY FOR BOARDS` when all of the following are true:

```text
connected == true
jobActive == true
outputOn == true
productionState == "JobActive"
partNumber == Plex active PartNumber
```

Because `JobActive` is only set after recipe/readback verification succeeds, a failed verification cannot produce a READY state.

## API Configuration

The application reads these environment variables:

```text
SPPS_RECIPE_FILE
SPPS_API_URL
SPPS_API_KEY
```

### Recipe file

If `SPPS_RECIPE_FILE` is not defined, the application looks for:

```text
PowerSupplyRecipes.xlsx
```

beside the executable.

A UNC network path can be used, for example:

```text
\\server\production\PowerSupplyRecipes.xlsx
```

### API binding

Default:

```text
SPPS_API_URL=http://127.0.0.1:5187
```

For a production workstation that must accept Plex AO requests from another machine, configure an appropriate workstation address or bind address.

Example:

```text
SPPS_API_URL=http://0.0.0.0:5187
```

### API security

When the API is bound beyond loopback, `SPPS_API_KEY` is required. The application refuses to expose the control API to a non-loopback interface without an API key.

Plex AO should send:

```http
X-API-Key: <configured secret>
```

The health endpoint remains available for basic service monitoring.

For plant-network deployment, also restrict Windows Firewall access to only the expected Plex/AO runtime or approved management hosts.

## USB Driver Requirement

The power supply communicates through a Silicon Labs CP210x USB-to-UART bridge. Windows must have the Silicon Labs CP210x Virtual COM Port driver installed so the device appears as a COM port.

Official Silicon Labs driver page:

https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers

The driver is intentionally not bundled in this repository.

Verify in Windows Device Manager under:

```text
Ports (COM & LPT)
```

You should see an entry similar to:

```text
Silicon Labs CP210x USB to UART Bridge (COMx)
```

## Standalone WPF Features

The engineering/service UI currently supports:

- COM-port discovery
- friendly USB serial names through WMI
- configurable baud rate
- connect / safe disconnect
- remote / local mode
- manual voltage/current setpoints
- output ON / OFF
- automatic V/I readback
- CV / CC display
- calculated power
- optional CSV logging
- response-paced serial polling
- buffered handling of partial and multiple serial frames

## Serial Protocol

Current default communication settings:

```text
9600 baud
8 data bits
No parity
1 stop bit
No handshake
```

Commands are ASCII frames in this general form:

```text
<XXXXXXXXXXX>
```

where the command payload is 11 characters.

Implemented functions include:

- remote mode
- local mode
- output ON
- output OFF
- voltage setpoint
- current setpoint
- voltage query
- current query

Setpoints are encoded using the PSU's six-digit numeric format.

## Communication Reliability

Serial receive handling buffers incoming data until complete `<...>` frames are available. This supports partial USB/UART reads and multiple frames received in one callback.

Readback requests are response-paced and serialized to reduce command overlap. Preferred and fallback query variants are available for voltage/current readback.

On normal application shutdown or disconnect, the application attempts to turn the output off and return the power supply to local mode before closing the COM port.

## Building

Requirements:

- Windows 10 or Windows 11
- .NET 8 SDK
- Visual Studio 2022 or another .NET 8-capable environment
- Silicon Labs CP210x VCP driver for hardware operation

Target framework:

```text
net8.0-windows
```

Restore:

```powershell
dotnet restore SPPS2001D_Pro.sln
```

Release build:

```powershell
dotnet build SPPS2001D_Pro.sln --configuration Release
```

The project continues to build through the original `SPPS2001D_Pro.sln`; the Plex REST API is hosted inside the same application.

## Continuous Integration

GitHub Actions performs a Windows Release build on the active development branch and stable branch as configured in `.github/workflows/build.yml`.

Software compilation can be validated in CI, but physical CP210x/PSU communication and production verification behavior must be tested on real hardware.

## Plex AO Integration Guide

See:

```text
PLEX_AO_INTEGRATION.md
```

for the intended Automation & Orchestration calls, operator-screen state logic, and job lifecycle.

## Safety

This application controls physical electrical equipment.

The software provides recipe validation, output-off behavior, network API protection, and live readback verification, but it must not be the only protection against unsafe voltage/current conditions.

Production deployments should also use appropriate hardware protections, PSU limits, fixture design, operator procedures, network controls, and validated recipes.

## Planned Improvements

Remaining high-priority work includes:

- hardware validation of verification tolerances and startup timing
- configurable absolute PSU voltage/current limits based on the exact supply model
- stronger request/response correlation in the serial layer
- strict malformed-frame rejection and communication diagnostics
- production audit logging
- explicit Manual vs Plex control-source indication
- final Plex AO operator-screen implementation and commissioning
