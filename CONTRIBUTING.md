# Contributing to FanPilot

Thanks for helping. Reports from laptops other than the ones already tested are especially useful.

## Reporting a bug or a laptop

- **Bug:** open a [bug report](https://github.com/MarceloGoluboff/FanPilot/issues/new?template=bug_report.yml).
- **Your laptop works, or doesn't:** open a
  [compatibility report](https://github.com/MarceloGoluboff/FanPilot/issues/new?template=compatibility_report.yml).

Either way, include the output of **Settings > Diagnostics > Copy diagnostics**. It has the model,
BIOS, driver version, fan setup results and recent log, with your user name removed.

## Building

You need the .NET 8 SDK, and Inno Setup 6 to build the installer.

```
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup
dotnet test tests\FanPilot.Tests
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

## Working on the window without ASUS hardware

```
net stop FanPilot
src\FanPilot\bin\Debug\net8.0-windows10.0.19041.0\win-x64\FanPilot.exe --host --simulate=2
```

Then start FanPilot normally; it connects to the simulated host. `--simulate=3` adds a third fan
that ignores commands, like some real models.

## Pull requests

- Keep changes focused, and match the style of the surrounding code (see `.editorconfig`).
- Add or update tests in `tests/FanPilot.Tests` for engine, curve and calibration logic.
- Anything that writes to the fans needs a clear path back to ASUS control on every exit,
  including errors and cancellation.
- `dotnet test` must pass and the build must have no warnings. CI checks both.
- Add a line to `CHANGELOG.md` under "Unreleased".

## Code of conduct

Be kind and constructive. Disrespectful behaviour in issues or pull requests isn't welcome.
