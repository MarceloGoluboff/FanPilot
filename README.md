# FanControl

Fan control for ASUS laptops, built for a Vivobook Q530VJ. It works like
[Karmel0x/AsusFanControl](https://github.com/Karmel0x/AsusFanControl), with a Fluent UI, a tray icon,
a temperature curve, and a real installer. No UAC prompts after install.

## Modes

- **ASUS Auto**: the firmware controls the fans (test mode off).
- **Manual**: a fixed duty for all fans, with Silent 25%, Balanced 45%, Performance 70% and Max 100%
  presets.
- **Curve**: duty follows CPU temperature. Speeds up right away, slows down only after the CPU is 3 °C
  cooler for 5 s.

Measured on the Q530VJ (both fans, settled): 10% ≈ 1,100 RPM, 25% ≈ 2,500 (about ASUS's own idle),
45% ≈ 3,700, 70% ≈ 5,000. At 5% both fans stall, so the app never goes below 15%.

Safety:
- Thermal override to 100% at a set CPU temperature (default 95 °C, released 10 °C lower).
- If the CPU temperature can't be read for 5 s in Manual or Curve, the fans go back to ASUS.
- Fixed duties are rewritten every 30 s and after sleep, since the EC can drop test mode.
- Control goes back to ASUS whenever the service stops or crashes. On release the fans are parked
  at 40% first, because a fan keeps its last test duty until the EC's next update.
- The override watches the CPU only. The GPU shares the cooling, but its temperature isn't read.

The 40% "Forbid unsafe" floor in AsusFanControl has no stated reason, and is louder than ASUS's own idle.

## How it's put together

ASUS System Control Interface driver 3.1.41.0 and later only accept fan calls from a process
running as **LocalSystem**; an elevated admin process gets `-1` back. So the app is split in two,
one exe with two roles:

- **`FanControl` service** (`FanControl.exe --service`, LocalSystem, automatic start). Loads
  `AsusWinIO64.dll` from the ASUS driver store
  (`C:\Windows\System32\DriverStore\FileRepository\asussci*\ASUSSystemAnalysis\`, newest wins),
  runs the fan engine, and keeps the fan config in its own profile,
  `C:\Windows\System32\config\systemprofile\AppData\Local\FanControl\` (not ProgramData, where
  standard users could plant links for SYSTEM to follow). It retries every 10 s if the ASUS driver
  isn't up yet at boot.
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
| `--diag` | Hardware check: fans, RPM, temp, 70% for 5 s, then ASUS control. Run as SYSTEM with the service stopped (`net stop FanControl`). Writes `diag.txt` to the service's data folder. |
| `--diag --sweep` | Also steps 60% down to 5% to find where the fans stall. |

Logs: `service.log` in the service's data folder, `%AppData%\FanControl\log.txt` for the window.

The app adds its own sign-in entry (HKCU Run) the first time it opens. The installer writes one
too, but an installer started from inside a packaged app (MSIX) writes into that app's private
registry view, not yours.
