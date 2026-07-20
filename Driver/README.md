# Comote Virtual HID

Comote Virtual HID is the optional input backend for Comote input mode 2. It is a transparent KMDF HID source driver built on the Windows Virtual HID Framework (VHF). It exposes a Comote-named keyboard and absolute mouse and accepts reports only through the Comote control interface.

## Input modes

- Mode 1: Windows `SendInput` (default, no driver required)
- Mode 2: Comote Virtual HID (optional kernel driver)

The driver uses Comote's own interface GUID and identifies itself as `Comote Virtual HID Keyboard and Mouse`. It does not copy another manufacturer's identity and contains no anti-cheat bypass or concealment behavior.

## Build

Use Visual Studio 2026 with the Desktop development with C++ workload and the matching WDK, or download the unsigned developer artifact from the `Build Comote Virtual HID` GitHub Actions workflow. The repository pins the WDK NuGet packages in `packages.config`.

Public Windows installation requires a Microsoft-signed driver package. Do not enable Windows test-signing mode on a normal gaming PC just to install a development build. Keep mode 1 selected until a production-signed package is available.

## Package layout

The client package must place these files next to `ComoteClient.exe`:

- `ComoteVirtualHidInstaller.exe`
- `ComoteVirtualHid.inf`
- `ComoteVirtualHid.sys`
- `ComoteVirtualHid.cat`

The installer is idempotent: it reuses an existing `Root\\ComoteVirtualHid` device rather than creating duplicates.