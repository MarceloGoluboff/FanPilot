# Security policy

FanPilot installs a Windows service that runs as **LocalSystem** and accepts commands from users
signed in to the PC over a named pipe. A bug there could let a local user do more than change fan
speeds, so security reports are taken seriously.

## Reporting a vulnerability

Please **don't** open a public issue. Report it privately through
[GitHub Security Advisories](https://github.com/MarceloGoluboff/FanPilot/security/advisories/new).

Include what you found, the steps to reproduce it, and the FanPilot version. You'll get a reply
within a week, and a fix or a plan as soon as possible after that. Credit is given in the release
notes unless you'd rather not.

## Scope

In scope:
- the `FanPilot` service
- its pipe protocol and the files it writes
- the installer and uninstaller
- anything that lets a standard user gain privileges or affect other users

Out of scope:
- ASUS's own driver and `AsusWinIO64.dll`; report those to ASUS
- the fact that any signed-in user can change fan settings, which is intended

## Supported versions

Only the latest release gets security fixes.
