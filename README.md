# SPPS2001D Pro Power Supply Controller

A Windows desktop application for controlling a KUAIQUA/SPPS2001D programmable DC power supply through its Silicon Labs CP210x USB-to-UART interface.

The project is being developed first as a reliable standalone power-supply control application, with the architecture designed to support future factory-automation integration, including Plex / Rockwell Automation.

## Current Features

- Windows WPF desktop interface
- Automatic discovery of serial/COM ports
- Friendly USB serial device names through Windows WMI
- Preference for common USB-UART devices such as Silicon Labs CP210x
- Configurable serial baud rate
- Connect and safe-disconnect controls
- Remote and local operating modes
- Set voltage and current from the GUI
- Output ON / OFF control
- Automatic voltage and current readback
- CV / CC operating-mode indication
- Live voltage, current, and calculated power display
- Optional CSV logging
- Response-paced serial polling to avoid overlapping commands
- Buffered serial-frame handling for partial or multiple USB/UART reads

## USB Driver Requirement

This application communicates with the power supply through a Silicon Labs CP210x USB-to-UART bridge. Windows must have the **Silicon Labs CP210x USB to UART Bridge Virtual COM Port (VCP) driver** installed so the device appears as a standard COM port.

Silicon Labs states that the CP210x VCP driver is required when using CP210x devices as a Virtual COM Port.

Official driver download:

https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers

The driver installer is intentionally **not bundled in this repository**. Download the current version directly from Silicon Labs so that the appropriate supported driver is used.

### Verify the driver in Windows

1. Connect the power supply to the PC with USB.
2. Open **Device Manager**.
3. Expand **Ports (COM & LPT)**.
4. Look for a device similar to:

```text
Silicon Labs CP210x USB to UART Bridge (COMx)
```

5. Note the COM-port number and confirm that the application can see the same port.

If the CP210x device does not appear under **Ports (COM & LPT)**, install or reinstall the Silicon Labs VCP driver and reconnect the USB cable.

Windows may install a compatible driver automatically through Windows Update on some systems, but installing the current Silicon Labs VCP package directly is the recommended troubleshooting path when the device is not detected correctly.

## Architecture

The application is intentionally separated into layers so that the power-supply control logic is not tied directly to the WPF user interface.

```text
WPF Desktop UI
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

`PowerSupplyController` is the application-level interface for the power supply. It handles operations such as connection management, setpoints, output state, polling, and readback processing.

`SerialService` handles the lower-level COM-port and protocol transport.

This separation is important because future automation interfaces can use the same controller without having to automate or depend on the WPF GUI.

## Future Plex / Rockwell Automation Integration

A long-term goal is to allow Plex / Rockwell Automation to control the power supply for manufacturing and test processes.

The intended architecture is:

```text
                    +--> WPF Desktop UI
                    |
PowerSupplyController
                    |
                    +--> Local Automation/API Bridge <-- Plex / Rockwell Automation
                    |
                    v
               SerialService
                    |
                    v
                  CP210x
                    |
                    v
                Power Supply
```

The automation layer could eventually expose operations such as:

```text
SetVoltage(24.000)
SetCurrent(0.500)
OutputOn()
OutputOff()
GetVoltage()
GetCurrent()
GetStatus()
```

The exact Plex interface has not yet been selected. A local REST API or other local bridge can be added later without changing the underlying power-supply communication layer.

## Serial Protocol

The current implementation communicates at 9600 baud by default using 8 data bits, no parity, and one stop bit (8N1).

Commands are ASCII frames in the following general form:

```text
<XXXXXXXXXXX>
```

where the payload is 11 characters.

The application currently implements commands for:

- Remote mode
- Local mode
- Output ON
- Output OFF
- Voltage setpoint
- Current setpoint
- Voltage readback
- Current readback

Setpoints are encoded using the supply's six-digit numeric format.

## Communication Reliability

USB serial communication is handled asynchronously. The application accumulates incoming serial data until complete `<...>` frames are available, allowing it to handle partial reads and multiple frames received in a single callback.

Readback requests are response-paced rather than sent as a rapid stream. Current and voltage queries are serialized, and fallback query forms are available if the preferred query does not produce a response within the configured timeout.

On a normal disconnect, the application attempts to turn the output OFF and return the supply to local mode before closing the COM port.

## Development Branches

- `main` — stable version
- `improvements` — active development and architectural improvements

New functionality is developed and validated on `improvements` before being considered for the stable branch.

## Building

### Requirements

- Windows 10 or Windows 11
- Silicon Labs CP210x VCP driver
- .NET 8 SDK
- Visual Studio 2022 or another .NET 8-capable development environment

The application targets:

```text
net8.0-windows
```

### Command Line

Restore dependencies:

```powershell
dotnet restore SPPS2001D_Pro.sln
```

Build a Release version:

```powershell
dotnet build SPPS2001D_Pro.sln --configuration Release
```

The compiled application will be produced under the project's `bin/Release/net8.0-windows` directory.

## Continuous Integration

GitHub Actions performs a Windows Release build to verify that committed code restores and compiles successfully.

CI currently runs for the stable and active-development branches.

## Planned Improvements

Near-term development is focused on making the application suitable for dependable standalone bench and production use. Planned work includes configurable voltage/current safety limits, stricter protocol validation, communication diagnostics and statistics, command/readback confirmation, improved asynchronous command handling, connection-health monitoring, presets, and clearer indication of manual versus automation control.

A future startup diagnostic is also planned so the application can provide a clearer message when no compatible CP210x/USB serial device is detected, including guidance to verify the VCP driver installation.

Longer-term work will add a local automation interface suitable for Plex / Rockwell Automation while continuing to use the same `PowerSupplyController` used by the desktop application.

## Hardware Testing

GitHub Actions can validate compilation and software-level behavior, but communication with the physical power supply and CP210x interface must be validated on actual hardware.

When changing protocol timing, readback behavior, or output-control logic, test the development branch with the physical supply before promoting those changes to `main`.

## Safety

This software can control the output of a physical DC power supply. Verify voltage and current settings before enabling the output, and do not rely on the application as the only protection against unsafe electrical conditions.

Future releases are intended to add configurable software limits and additional command verification, but hardware protection and appropriate operating procedures remain necessary.

## Project Status

This project is under active development. The current focus is a stable standalone Windows controller, followed by production-oriented diagnostics and safety features, and eventually Plex / Rockwell Automation integration.
