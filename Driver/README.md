# CampusNetTrafficNet driver

This folder contains the planned WFP byte-counter driver for CAUCNet Traffic.

The driver exposes `\\.\CampusNetTrafficNet` and supports:

- `IOCTL_CNT_GET_COUNTERS`: returns `CNT_COUNTERS`
- `IOCTL_CNT_RESET_COUNTERS`: resets both counters

The current desktop app and traffic service keep working without the driver.
`CampusNetTraffic.TrafficService` tries the driver first for global counters
and falls back to `GetIfTable2` when the driver is not installed or not running.

Build requirements:

- Visual Studio 2022 C++ build tools
- Windows Driver Kit 10.0.26100 or newer
- Driver signing/test-signing setup for local installation

Local test-signing flow:

1. Open PowerShell as Administrator.
2. Run `powershell -ExecutionPolicy Bypass -File .\scripts\new-driver-test-certificate.ps1 -EnableTestSigning`
3. Reboot Windows so test-signing mode takes effect.
4. Run `powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 -TestSignDriver`
5. Install the generated setup package as Administrator.

Production distribution requires a Microsoft-accepted kernel driver signature.
