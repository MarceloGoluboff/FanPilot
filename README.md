# FanControl

Fan control for ASUS laptops, built for a Vivobook Q530VJ. It works like
[Karmel0x/AsusFanControl](https://github.com/Karmel0x/AsusFanControl), with a Fluent UI, a tray icon,
a temperature curve, and a real installer. No UAC prompts after install.

## Modes

- **ASUS Auto**: the firmware controls the fans (test mode off).
- **Manual**: a fixed duty for all fans, with Silent, Balanced, Performance and Max presets.
- **Curve**: duty follows CPU temperature. Speeds up right away, slows down only after the CPU is 3 °C
  cooler for 5 s.

Safety: optional 40% floor, thermal override to 100% at a set temperature (default 95 °C, released
10 °C lower), fixed duties rewritten every 30 s and after sleep, and control handed back to ASUS
whenever the service stops.

## How it's put together

ASUS System Control Interface driver 3.1.41.0 and later only accept fan calls from a process
running as **LocalSystem**; an elevated admin process gets `-1` back. So the app is split in two,
one exe with two roles:

- **`FanControl` service** (`FanControl.exe --service`, LocalSystem, automatic start). Loads
  `AsusWinIO64.dll` from the ASUS driver store
  (`C:\Windows\System32\DriverStore\FileRepository\asussci*\ASUSSystemAnalysis\`, newest wins),
  runs the fan engine, and keeps the fan config in `%ProgramData%\FanControl\fan-config.json`.
  It retries every 10 s if the ASUS driver isn't up yet at boot.
- **The window and tray icon** (`FanControl.exe`, runs as you, unelevated). Talks to the service
  over the named pipe `MarceloGoluboff.FanControl` (newline-delimited JSON; only the console user,
  admins and SYSTEM can open it). Closing it doesn't change the fans.

The Start menu shortcut and the window share the AppUserModelID `MarceloGoluboff.FanControl`, so
the window groups under a taskbar pin.

## Requirements

- 64-bit Windows, an ASUS laptop where MyASUS Fan Diagnosis works.
- The ASUS System Control Interface driver (installed with MyASUS).

## Build

```
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

The installer lands in `dist\`. After installing, pin FanControl from Start.

## Command line

| Flag | Does |
| --- | --- |
| `--tray` | Start with only the tray icon (used by the sign-in Run entry). |
| `--service` | Service entry point; only the Service Control Manager should use it. |
| `--install-service` | Creates or updates and starts the service. Elevated; the installer runs it. |
| `--diag` | Hardware check: fans, RPM, temp, 70% for 5 s, then ASUS control. Must run as SYSTEM. Writes `%ProgramData%\FanControl\diag.txt`. |

Logs: `%ProgramData%\FanControl\service.log` (service), `%AppData%\FanControl\log.txt` (window).
