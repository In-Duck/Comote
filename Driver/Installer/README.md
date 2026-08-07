# Comote Phase 2 native driver installer

This directory contains the fail-closed x64 Win32 installer for
`ComoteVirtualHidPhase2`. It replaces the development-only WDK/DevGen install
path. The installer does not enable `TESTSIGNING`, install trust certificates,
change Secure Boot or memory integrity, or submit HID input reports.

The supported target is Windows 10 x64 build `19045` (any UBR). Native build,
install, removal, reboot, sleep/resume, and Driver Verifier testing remain
restricted to a disposable VM until every release gate passes.

## Release trust root and build order

The release manifest is not caller-trusted. Its exact raw bytes are pinned into
the installer at compile time. The checked-in
`ComoteReleaseManifestPin.h` is intentionally unpinned; an executable built
with that header rejects every install.

Create a release in this order:

1. Produce the final INF, CAT, and SYS in an otherwise empty package directory.
2. Create the strict manifest shown below, using each final file's byte size and
   SHA-256.
3. Generate `ComoteReleaseManifestPin.h` from that exact manifest and package:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Build-ComoteReleasePinnedInstaller.ps1 `
  -ManifestPath C:\Comote\Release\package-manifest.txt `
  -PackageDirectory C:\Comote\Release\DriverPackage
```

4. Build `ComoteDriverInstaller.vcxproj` only after the generated header is in
   place. Do not modify the manifest, INF, CAT, or SYS afterward.
5. Ship the installer, the exact pinned manifest, and the exact three-file
   package as one release.

The helper rejects UNC/device and non-fixed-volume paths, reparse ancestors,
reparse files, hard links, subdirectories, extra or mis-cased files, zero
sizes, and size/hash mismatches before it writes a pinned header. File identity,
size, and SHA-256 are taken from the same deny-write/delete handle.

The manifest is strict ASCII and has exactly these 12 non-empty lines in this
order:

```text
COMOTE-PHASE2-PACKAGE-MANIFEST-V1
HardwareId=ROOT\COMOTEVIRTUALHID_PHASE2
RootInstanceId=ROOT\COMOTEVIRTUALHID_PHASE2\COMOTE_PHASE2
ServiceName=ComoteVirtualHidPhase2
Provider=Comote
PackageFiles=ComoteVirtualHidPhase2.inf,ComoteVirtualHidPhase2.cat,ComoteVirtualHidPhase2.sys
InfSize=<positive decimal byte count>
InfSha256=<64 uppercase hex characters>
CatSize=<positive decimal byte count>
CatSha256=<64 uppercase hex characters>
SysSize=<positive decimal byte count>
SysSha256=<64 uppercase hex characters>
```

## Validation and staging

Before PnP mutation, the installer:

- compares the SHA-256 of the manifest's raw bytes to the compile-time pin using
  a fixed-length comparison;
- accepts only the exact three regular package files on a fixed local volume;
- rejects UNC/device paths, reparse ancestors, reparse files, hard links,
  additional files/directories, size mismatches, and SHA-256 mismatches;
- parses the INF through SetupAPI and follows the active Manufacturer, Models,
  DDInstall, Services, HW, WDF, copy, source, and destination sections;
- rejects extra sections/models/services/files/filters and rejects
  `Include`, `Needs`, `AddReg`, `DelReg`, and `CopyInf` outside the one exact VHF
  lower-filter directive;
- verifies the catalog signature and exact SYS membership through the catalog
  trust path; an embedded SYS signature is not required;
- copies only verified files into the protected local staging directory
  `%ProgramData%\ComoteDriverInstaller\Phase2\Staging\Phase2`; and
- revalidates final paths, link counts, sizes, hashes, INF semantics, and catalog
  trust after staging.

The dedicated ProgramData installer directory and transaction receipt are owned
by built-in Administrators and use a protected DACL granting full control only
to LocalSystem and built-in Administrators. The installer never changes the ACL
on the application's shared `%ProgramData%\Comote` directory. The production
state path is fixed at:

```text
%ProgramData%\ComoteDriverInstaller\Phase2\phase2-installer.state
```

`--state` is rejected by production builds. It exists only when the installer
is explicitly compiled with `COMOTE_INSTALLER_VM_TEST=1` for isolated VM tests.

## CLI

```powershell
ComoteDriverInstaller.exe install `
  --package C:\Comote\Release\DriverPackage `
  --manifest C:\Comote\Release\package-manifest.txt

ComoteDriverInstaller.exe status `
  --manifest C:\Comote\Release\package-manifest.txt

ComoteDriverInstaller.exe remove `
  --manifest C:\Comote\Release\package-manifest.txt
```

Every invocation ends with one machine-readable line:

```text
COMOTE_INSTALLER_RESULT code=<number> state=<token> message="<single line>"
```

All commands require elevation because the receipt is intentionally Administrator/System-only. Important exit codes are `0` success, `21` already installed and healthy, `23`
recovery required, and `36` reboot required. The V2 receipt records the
operation, transaction status, protected staging path, exact published INF,
boot ID, reboot requirement, manifest hash, identities, and all package hashes.
Interrupted install/remove operations are resumed or rolled back only after the
same fixed identities and hashes are revalidated.

## Exact install and removal

Installation stages the exact INF with `SetupCopyOEMInfW`, creates exactly
`ROOT\COMOTEVIRTUALHID_PHASE2\COMOTE_PHASE2`, performs a clean inventory check,
and binds the selected driver node with `DiInstallDevice`. It never performs a
global hardware-ID update or uses a force flag. Success requires:

- one healthy System-class root whose enumerator is `ROOT`;
- one exact Phase 2 control interface owned by that root;
- a running demand-start kernel service whose path is the verified Driver Store
  SYS;
- one healthy Keyboard-class descendant; and
- two healthy Mouse-class descendants (relative and absolute); and
- exactly those three VHF input descendants, with no unexpected extra child.

Removal uses `DiUninstallDevice` for the exact root and `DiUninstallDriverW` for
the exact recorded `oemN.inf`. The service is queried, optionally disabled and
stopped, and deleted through the same service handle only after its type, start
mode, binary path, hash, and catalog identity are verified. A running service
that cannot unload immediately records a protected reboot-required receipt and
resumes afterward. The verified service is removed before its Driver Store
package, so a crash cannot leave an unverifiable orphan. Wildcard/provider-based
removal is forbidden.

## Host-safe validation

Run the read-only source boundary:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Test-InstallerBoundary.ps1
```

The boundary checks required safety APIs/contracts, forbidden broad mutation
paths, the project XML, the unpinned default header, and malformed-manifest and
package-policy negative cases. A `/Zs /EHsc /W4 /WX` syntax-only compiler pass
may be run on the physical development host. Do not link, execute, install, or
load this driver/installer on the physical host.

A final release still requires the full signed-package install/status/remove,
reboot-resume, rollback, power-transition, and Driver Verifier matrix inside the
disposable Windows 10 `19045` VM.