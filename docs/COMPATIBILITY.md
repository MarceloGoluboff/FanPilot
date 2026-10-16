# Compatibility

FanPilot works on ASUS laptops where **Fan Diagnosis in MyASUS works**, because it uses the same
ASUS library. Whether that library works depends more on the ASUS System Control Interface driver
version than on the model.

## Tested with FanPilot

| Model | BIOS | ASUS SCI driver | Fans | Fan setup result | Notes |
| --- | --- | --- | --- | --- | --- |
| ASUS Vivobook 15X (Q530VJ, i7-13620H, RTX 3050) | K6502VJQ.306 | 3.1.72.0 | 2 | About 6,500 / 7,100 RPM at 100%, 1,140 at 10%. Both stall at 5% and restart at 10%. Minimum 15%. | Both fans controllable. Above about 70 °C the firmware keeps control and ignores set speeds. |

## Reported working with AsusFanControl

FanPilot talks to the fans the same way as
[AsusFanControl](https://github.com/Karmel0x/AsusFanControl), so its users' reports are a good
starting point. They include Vivobook, Zenbook, TUF Gaming, ROG Strix, ROG Zephyrus and ROG Flow
models, and an ASUS PN53 mini PC. These haven't been tested with FanPilot yet.

## Known differences between models

- **Number of fans.** Thin Vivobook and Zenbook models usually have 1 fan, TUF and ROG models 2,
  and some ROG models 3. FanPilot reads the count from the driver.
- **Fans that ignore commands.** On some two-fan TUF models only one fan responds. Fan setup
  detects this and leaves that fan with ASUS.
- **CPU temperature.** Some models report 0 through the ASUS library. FanPilot then falls back to
  the ACPI thermal zone, which is coarser.
- **Hot laptops.** At least some firmware ignores set speeds when the CPU is hot, and keeps its
  own. That's why fan setup needs the CPU below 70 °C.
- **Fans set one at a time.** On the Q530VJ, a fan set on its own (with the other on ASUS control)
  ignored most speeds, but both followed when set together. FanPilot always sets them together.
- **Older drivers.** Drivers before 3.1.41.0 accept any admin process. Later ones need LocalSystem,
  which FanPilot's service handles.
- **ARM64 (Snapdragon) laptops** aren't supported: the ASUS library is x64 only.

## Add your laptop

Open a [compatibility report](https://github.com/MarceloGoluboff/FanPilot/issues/new?template=compatibility_report.yml).
In FanPilot, go to **Settings > Diagnostics > Copy diagnostics** and paste the result into the
report. It includes the model, BIOS, driver version and fan setup results, with your user name
removed.
