# Third-party notices

FanPilot's installer includes the components below. Each is used under the license shown.

## .NET runtime and libraries

The .NET 8 runtime, WPF, and the `System.Management`, `System.Diagnostics.PerformanceCounter` and
`System.ServiceProcess.ServiceController` packages.

- License: MIT
- Copyright (c) .NET Foundation and Contributors
- https://github.com/dotnet/runtime, https://github.com/dotnet/wpf

## WPF UI

- License: MIT
- Copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors. https://lepo.co/
- https://github.com/lepoco/wpfui

## H.NotifyIcon

- License: MIT
- Copyright (c) 2020 havendv
- https://github.com/HavenDV/H.NotifyIcon

The full MIT license text is the same as in [LICENSE](LICENSE), with each copyright holder above in
place of FanPilot's.

## Not included: AsusWinIO64.dll

FanPilot talks to the fans through `AsusWinIO64.dll`, which is ASUSTeK software. FanPilot does not
include or redistribute it. At run time it loads the copy that the ASUS System Control Interface
driver installs in the Windows driver store.

## Acknowledgements

[Karmel0x/AsusFanControl](https://github.com/Karmel0x/AsusFanControl) (MIT, Copyright (c) 2023
Karmel0x) identified the `AsusWinIO64.dll` entry points FanPilot uses and showed that the ASUS
System Control Interface driver needs a LocalSystem caller from version 3.1.41.0. No code was copied.

The Linux kernel's `asus-wmi` documentation and AsusFanControl's issue tracker were useful
references for how ASUS fan control varies between models.
