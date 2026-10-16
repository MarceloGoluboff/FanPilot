# Changelog

All notable changes to FanPilot are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [2.0.0] - Unreleased

First public release, renamed from FanControl to FanPilot.

### Added
- **Fan setup**
  - Offered on first launch, and re-runnable from Settings.
  - Measures each fan's stall point, start-up speed and ASUS idle speed.
  - Sets the minimum speed and Silent preset for your laptop.
  - Detects fans that don't follow commands.
- **GPU temperature**, from the same source Task Manager uses, so a sleeping GPU isn't woken. A
  GPU thermal override (default 87 °C), and an option for the curve to follow the hotter of CPU and GPU.
- **ACPI thermal zone fallback** when the ASUS library reports no CPU temperature.
- **Sleep:** fans go back to ASUS before sleep and are reapplied after wake.
- **Conflict warning** when G-Helper or AsusFanControl is also running.
- **Copy diagnostics** button for bug reports, with user paths removed.
- **Fan counts of 1, 2 or 3**, with clear labels.
- **Developer tools:** a simulated fan backend (`--host --simulate`), unit tests, and CI.

### Changed
- The tray icon at sign-in is set up for every user, and each user can turn it off.
- Before fan setup runs, the minimum speed is a conservative 30%.
- The installer refuses ARM64 Windows and warns if the ASUS driver is missing.
- Every call to ASUS's library runs on one thread. Calls from other threads stalled for about 20
  seconds and had their fan speed writes ignored.

## [1.1.0] - 2026-10-15

### Changed
- Minimum speed is based on measured fan behaviour instead of a fixed 40%.
- The window fits the screen.

### Fixed
- Handing fans back to ASUS parks them at 40% first. Releasing from a low speed could leave them
  stopped until the firmware's next update.
- Manual mode hands back to ASUS if the CPU temperature can't be read.
- The uninstaller can no longer kill the service before it restores the fans.
- Service data moved out of ProgramData, which standard users can write.
- Pipe, timer and config handling are more robust.

## [1.0.0] - 2026-10-15

### Added
- First version, as FanControl:
  - a LocalSystem service that drives the fans through ASUS's `AsusWinIO64.dll`
  - a tray app with ASUS Auto, Manual and Curve modes
  - an Inno Setup installer
