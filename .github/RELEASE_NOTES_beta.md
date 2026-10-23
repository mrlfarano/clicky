<!-- Release notes template for a win-v<semver>-beta.N release on the win-beta channel (WS-21).
     Copy it into the GitHub release body and fill every <...> before telling testers. -->
# Clicky for Windows <tag> (beta)

## What changed

- <change>

## Installing

Download `Clicky-win-beta-Setup.exe` and run it. It installs for your user only and needs no admin rights.

Clicky is signed with the same certificate on every release, but it is new to Microsoft SmartScreen, so until it builds reputation Windows will likely warn you:

1. "Windows protected your PC" appears. Click **More info**.
2. Check that the publisher is Clicky's signer, then click **Run anyway**.

Installed copies update themselves, so you see this only when you run Setup.exe.

## QA

- SmartScreen-on-clean-VM: <warned | did not warn>, <Windows build of the VM>, <date>
- Promote this channel to `win` once releases stop warning on a clean VM, or when the owner accepts the warning.
