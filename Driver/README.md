# Comote Virtual HID

Comote Virtual HID is the optional input backend for Comote input mode 2. It is a transparent KMDF HID source driver built on the Windows Virtual HID Framework (VHF). It exposes a Comote-named keyboard and absolute mouse and accepts reports only through the Comote control interface.

## Input modes

- Mode 1: Windows `SendInput` (default, no driver required)
- Mode 2: Comote Virtual HID (optional kernel driver)

The driver uses Comote's own interface GUID and identifies itself as `Comote Virtual HID Keyboard and Mouse`. It does not copy another manufacturer's identity and contains no anti-cheat bypass or concealment behavior.

## Build and team test package

The `Build Comote Virtual HID` GitHub Actions workflow builds the driver, creates a short-lived team test certificate, signs the catalog, and publishes a 90-day artifact named `ComoteVirtualHid-test-signed-team-build`.

The artifact includes administrator launchers for installation and full cleanup. Follow `TEST_MODE_GUIDE_KO.md`. The installer enables Windows test-signing only after confirming that Secure Boot is not active; it never changes Secure Boot itself.

Public Windows installation still requires a Microsoft-signed production driver package. The distribution script explicitly rejects the team test certificate so this package cannot be included accidentally in a public release.

## Package layout

The test package contains:

- `ComoteVirtualHidInstaller.exe`
- `ComoteVirtualHid.inf`
- `ComoteVirtualHid.sys`
- `ComoteVirtualHid.cat`
- `ComoteTeamTest.cer`
- install and cleanup scripts

The installer is idempotent: it reuses an existing `Root\\ComoteVirtualHid` device rather than creating duplicates. The uninstall command removes every matching Comote root device.
