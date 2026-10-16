# Changelog

All notable changes to FanPilot are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-10-15

First public release.

### Added
- **Three modes**
  - **ASUS Auto**: the laptop's firmware controls the fans.
  - **Manual**: one fixed speed, with Silent, Balanced, Performance and Max presets.
  - **Curve**: speed follows temperature along a curve you drag into shape.
- **Fan setup**
  - Offered on first launch, and re-runnable from Settings.
  - Measures your fans together: stall point, start-up speed and ASUS idle speed.
  - Checks they speed back up on command, then sets the minimum speed and the Silent preset.
  - Fans that don't follow commands stay with ASUS.
- **Temperatures**
  - CPU temperature from the ASUS library, with an ACPI thermal zone fallback.
  - GPU temperature from the same source Task Manager uses, so a sleeping GPU is never woken.
  - Thermal overrides that force 100%: CPU (default 95 °C) and GPU (default 87 °C).
  - The curve can follow the hotter of CPU and GPU.
- **Safety:** the fans go back to ASUS when the service stops or crashes, before sleep, when the
  CPU temperature can't be read, and when fan setup is cancelled. Before fan setup runs, the
  minimum speed is 30%.
- **No UAC prompts:** a LocalSystem service drives the fans, and the window and tray icon run as
  the user. The tray icon at sign-in is set up for every user, and each user can turn it off.
- **Conflict warning** when G-Helper or AsusFanControl is also running.
- **Copy diagnostics** for bug reports, with user paths removed.
- **Laptops with 1, 2 or 3 fans.**
- **Installer** for x64 Windows 10 and 11. It warns if the ASUS System Control Interface driver
  is missing.
- **For contributors:** a simulated fan backend (`--host --simulate`), unit tests, and CI.

## Before FanPilot

FanPilot grew out of FanControl, a private app for one ASUS Vivobook. FanControl had two
unreleased versions on 2026-10-15. Lessons carried over from them:

- **Measured minimum speed.** The fixed 40% floor was replaced by a minimum based on how the fans
  actually behave.
- **Parking before hand-back.** Fans are parked at 40% before control goes back to ASUS. Releasing
  them from a low speed could leave them stopped.
- **Service data location.** The service keeps its data out of ProgramData, which standard users
  can write.
- **Uninstall order.** The uninstaller stops the service before closing windows, so the fans are
  always restored.
