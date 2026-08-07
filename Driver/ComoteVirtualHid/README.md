# Comote Virtual HID

This directory contains the safety-first Phase 1 KMDF/VHF source-driver
skeleton.

## Phase 1 safety boundary

- The driver creates and starts one VHF device.
- The report descriptor declares a six-key-rollover keyboard and a relative
  five-button mouse.
- No input reports are submitted.
- No user-mode device interface, symbolic link, IOCTL queue, shared memory, or
  network path exists.
- VHF is deleted synchronously from the matching WDF device cleanup callback.
- Device callbacks are constrained to `PASSIVE_LEVEL`.
- The parent device is exclusive and cannot be opened from user mode.

This phase is intentionally not connected to `ComoteClient.exe`. A successful
build is not permission to install it on a daily-use PC.

## Build prerequisites

- A disposable Windows 10 Home 22H2 x64 development VM on build 19045
- Visual Studio 2022 with Desktop development with C++
- Current Windows SDK and WDK
- MSVC Spectre-mitigated libraries
- PowerShell 7 or Windows PowerShell 5.1

Create a clean VM snapshot, then run from a **non-elevated** Developer
PowerShell inside the disposable VM:

```powershell
.\Invoke-Phase1VmBuild.ps1 `
    -AcknowledgeDisposableVm `
    -SnapshotName "comote-phase1-clean"
```

The wrapper refuses unrecognized physical machines, Windows Server, Windows
versions other than Windows 10 Home build 19045, and non-x64 systems. It records
the installed update revision (UBR) in a JSON report under
`artifacts\phase1-reports`, runs `Test-Phase1Boundary.ps1`, builds the x64
Release driver, and performs INF validation. It does not enable test mode,
create a certificate, sign a package, or install the driver.

## Background CI build

GitHub Actions runs `.github/workflows/virtual-hid-phase1.yml` on the pinned
`windows-2025-vs2026` image. The workflow restores the pinned WDK NuGet
packages, compiles the driver, runs static analysis, validates the INF, creates
an unsigned catalog, and records SHA-256 hashes.

`Build-Phase1Ci.ps1` refuses to run unless `GITHUB_ACTIONS=true`. The CI
workflow never changes test mode, creates a certificate, signs a file, installs
a driver, loads a driver, or runs Driver Verifier. Its unsigned artifact is
retained for seven days for inspection only.

## Required gates before input submission is added

1. Release build completes with warnings treated as errors.
2. `InfVerif` passes.
3. `Inf2Cat` produces a catalog for the selected test OS matrix.
4. The catalog and SYS are test-signed outside the repository.
5. Installation and removal succeed on a disposable VM snapshot.
6. The VHF parent, keyboard, and mouse devices appear without warning icons.
7. Ten install/reboot/remove cycles leave no Comote device or Driver Store
   package behind.
8. Driver Verifier standard checks complete without a bug check.

Do not add an IOCTL handler until its fixed-size protocol, access-control list,
length validation, cancellation, rate limit, and malformed-input tests are
reviewed.

The Windows VM procedure is documented in `PHASE1_TEST_PLAN_KO.md`.
