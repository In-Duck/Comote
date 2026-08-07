# Comote Virtual HID preview release

This directory defines a fail-closed, **VM-only validation preview** for a
test-signed Comote Phase 2 driver. It is not a general end-user installer.
The only supported mutation target is a disposable VMware guest running a
Windows 10 Home-family 22H2 x64 edition, build 19045, any UBR.
Standard Windows 10 22H2 support has ended; the VM must be in an appropriately
supported/ESU test environment. This preview makes no production-support
claim for Windows 10.
The unified bundle is the **VMware validation-only candidate**, not the
two-machine deliverable. Only after its complete promotion evidence passes
does the workflow export a non-administrative Manager role ZIP and a separate
Client+Broker+driver role ZIP. Those exported roles remain preview artifacts,
not production or commercial packages.

The scripts never enable TESTSIGNING, never change Secure Boot, never change
HVCI or memory-integrity settings, and never install a private key. The guest
must already have an active test-signing boot, Secure Boot disabled, and
kernel-mode HVCI inactive. Those prerequisites must be prepared explicitly
outside this workflow and only after a clean VMware snapshot is recorded.
Restore that snapshot after validation.

Do not run a release build, install, uninstall, Broker self-test, driver status
command, E2E input test, or Driver Verifier command on the physical
development host. The physical host may run only
`Test-VirtualHidPreviewBoundary.ps1`, which parses and inspects source text.

## Release package layout

The build produces this exact authenticated shape:

```text
START HERE - Manager Hub.cmd
START HERE - Client Virtual HID.cmd
App/
  Client/
    ComoteClient.exe
    Start Comote Client Virtual HID.cmd
    ThirdParty/FFmpeg/
      LICENSE.LGPLv3.txt
      LICENSE.SIPSorceryMedia.FFmpeg.LGPL-2.1.txt
      LICENSE.FFmpeg.AutoGen.MIT.txt
      NOTICE.md
      SOURCE_OFFER.md
      manifest.json
  Manager/
    ComoteManager.exe
    Start Comote Manager Hub.cmd
    ThirdParty/FFmpeg/
      LICENSE.LGPLv3.txt
      LICENSE.SIPSorceryMedia.FFmpeg.LGPL-2.1.txt
      LICENSE.FFmpeg.AutoGen.MIT.txt
      NOTICE.md
      SOURCE_OFFER.md
      manifest.json
  Broker/
    Comote.InputBroker.exe
Driver/
  Package/
    ComoteVirtualHidPhase2.inf
    ComoteVirtualHidPhase2.cat
    ComoteVirtualHidPhase2.sys
  package-manifest.txt
  ComoteDriverInstaller.exe
Trust/
  ComotePhase2Test.cer
THIRD_PARTY_NOTICES/
  DOTNET_DIRECT_DEPENDENCIES.md
  NUGET_SBOM.json
  FFMPEG.md
  FFMPEG_ASSET_RECEIPT.json
  FFmpeg/
    LICENSE.LGPLv3.txt
    LICENSE.SIPSorceryMedia.FFmpeg.LGPL-2.1.txt
    LICENSE.FFmpeg.AutoGen.MIT.txt
    NOTICE.md
    SOURCE_OFFER.md
    manifest.json
Validation/
  Comote.MediaGate.exe
  REGRESSION_GATE.json
  MEDIA_GATE.json
  NuGetLocks/<source-relative-project>/packages.lock.json
Install-ComoteVirtualHidPreview.ps1
Uninstall-ComoteVirtualHidPreview.ps1
VirtualHidPreview.Common.ps1
README.md
release-manifest.json
```

`release-manifest.json` pins every other file by canonical relative path,
positive byte length, and SHA-256. It separately binds the Client, Manager,
and Broker roles to their final single-file executable hashes, original
filenames, and exact Authenticode signer. The `validationTools` entry pins the
fresh signed MediaGate, while `runtimePolicy` pins .NET 10.0.10 and the exact
restored `coreclr.dll` length/hash/FileVersion/ProductVersion. Its package role is
`validation-unified`. CMT1 is only the secure access-key format; it is not
used as an application identity or trust signal.

The ZIP SHA-256 and release-manifest SHA-256 are written outside the ZIP.
Obtain and compare both through an authenticated channel before executing any
script from an extracted bundle. The script that performs verification is
itself inside the bundle, so the external ZIP hash is the bootstrap trust
boundary.

## Preparing immutable Phase 2 inputs

Perform preparation inside the disposable signing/build VM, from a fresh
isolated copy of the final source after all Host, Viewer, Broker, InputCore,
driver, and embedded FFmpeg changes have settled.

1. Inventory and copy the exact root `global.json`, optional recognized
   NuGet/MSBuild configuration, all 12 source `packages.lock.json` files, the
   seven pure-test projects, release projects, driver sources, MediaGate, and
   packaging tools. The exact SDK is `10.0.302`; roll-forward/prerelease are
   disabled.
2. Before any driver build or signing, run
   `Invoke-VirtualHidPreviewRegressionGate.ps1`. It performs only locked
   restores, hashes every lock and `obj/project.assets.json`, runs seven pure
   suites and five boundary groups, builds the FinalValidation observer,
   executes the unsigned synthetic MediaGate, and requires valid JSON
   vulnerability scans with zero direct/transitive vulnerable packages. It
   also rejects legacy Host self-install/uninstall, SCM, machine-wide DPAPI,
   and embedded-default-password behavior.
3. The same gate writes `REGRESSION_GATE.json` and deterministic
   `NUGET_SBOM.json`. The SBOM proves lock `contentHash` equals the restored
   `.nupkg.sha512`, and records direct/transitive role use, clear license,
   HTTPS provenance, nuspec hash, and SHA512-sidecar hash for every resolved
   release dependency. These are completed mandatory release gates, not
   future work.
4. Run the Phase 2 VM build gate to create the unsigned package.
5. Run `Prepare-Phase2TestSigning.ps1` using the recorded snapshot name. It
   creates the exact signed package, public certificate, and protected signing
   receipt. It must report that TESTSIGNING was not changed.
6. Create the strict 12-line ASCII native package manifest from the final
   INF/CAT/SYS. Do not normalize or rewrite those bytes afterward.
7. In that isolated source copy, generate the manifest pin and build the x64
   Release native installer with
   `Driver/Installer/Build-ComoteReleasePinnedInstaller.ps1 -Build`.
   The helper writes its pin header, which is why the isolated copy is
   mandatory.
8. Record out-of-band SHA-256 values for the regression report, SBOM, signing
   receipt, strict driver
   manifest, and unsigned pinned native installer, plus the 40-hex signing
   certificate thumbprint.

`Build-VirtualHidPreviewRelease.ps1` accepts those pins, publishes the latest
Host, Viewer, Broker, and MediaGate source as self-contained single-file
`win-x64` applications with exact runtime `10.0.10`, locked mode, and no
restore. It signs those four applications and the pinned native installer,
stages the regression/SBOM/lock/MediaGate evidence, then creates the outer
release manifest and ZIP. It does not install, load, or test a driver.

Example parameter shape (values are intentionally placeholders):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Build-VirtualHidPreviewRelease.ps1 `
  -AcknowledgeDisposableVm `
  -ReleaseId 1.6.0-preview.virtualhid.1 `
  -SignedDriverPackageDirectory C:\VMRelease\phase2-test-signed `
  -Phase2SigningReceiptPath C:\VMRelease\test-signing-preparation.json `
  -ExpectedSigningReceiptSha256 <64-HEX> `
  -DriverManifestPath C:\VMRelease\package-manifest.txt `
  -ExpectedDriverManifestSha256 <64-HEX> `
  -PinnedNativeInstallerPath C:\VMRelease\ComoteDriverInstaller.exe `
  -ExpectedPinnedInstallerSha256 <64-HEX> `
  -RegressionGatePath C:\ComoteIsolated\ReleaseHandoff\REGRESSION_GATE.json `
  -ExpectedRegressionGateSha256 <64-HEX> `
  -NuGetSbomPath C:\ComoteIsolated\ReleaseHandoff\NUGET_SBOM.json `
  -ExpectedNuGetSbomSha256 <64-HEX> `
  -ExpectedSourceInventorySha256 <64-HEX> `
  -ExpectedCodeSigningCertificateThumbprint <40-HEX> `
  -SignToolPath 'C:\Program Files (x86)\Windows Kits\10\bin\<kit>\x64\signtool.exe' `
  -OutputDirectory C:\VMRelease\Final
```

The output directory must not already exist. Failed builds are deliberately
left for forensic inspection; the script performs no broad cleanup.

## Integrated source pin, build, promotion, and resume

From the final source tree, compute the canonical inventory without changing
source or machine state:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Get-VirtualHidPreviewSourceInventory.ps1 `
  -SourceRoot C:\ComoteFinal
```

Record the printed SHA-256 out of band. For a build-only isolated VM run,
invoke `New-VirtualHidPreviewReleaseInVm.ps1` with that exact pin:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\New-VirtualHidPreviewReleaseInVm.ps1 `
  -AcknowledgeDisposableVm `
  -SnapshotName clean-preview-base `
  -ReleaseId 1.6.0-preview.virtualhid.1 `
  -SourceRoot C:\ComoteFinal `
  -ExpectedSourceInventorySha256 <64-HEX> `
  -IsolatedWorkRoot C:\ComoteIsolated `
  -SignToolPath 'C:\Program Files (x86)\Windows Kits\10\bin\<kit>\x64\signtool.exe' `
  -OutputDirectory C:\VMRelease\Final
```

For the complete validation and promotion path, do not pre-run that build-only
command against the same output. The promotion command performs the fresh
isolated build on its first invocation and persists its exact phase under the
protected ProgramData promotion state:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Invoke-VirtualHidPreviewVmPromotion.ps1 `
  -AcknowledgeDisposableVm `
  -SnapshotName clean-preview-base `
  -ReleaseId 1.6.0-preview.virtualhid.1 `
  -SourceRoot C:\ComoteFinal `
  -ExpectedSourceInventorySha256 <64-HEX> `
  -IsolatedWorkRoot C:\ComoteIsolated `
  -SignToolPath 'C:\Program Files (x86)\Windows Kits\10\bin\<kit>\x64\signtool.exe' `
  -OutputDirectory C:\VMRelease\Final `
  -ControllerUser 'VMNAME\ExactInteractiveUser'
```

Rerun that same base command after each printed checkpoint, adding only the
exact acknowledgement switches and phrases requested by the script. The
workflow resumes from its protected receipt; it does not repeat a completed
phase. It advances through twenty-one ordered phases, and every `await-` phase
stops for an explicit manual action:

1. `pre-install-gates` — fresh source inventory, isolated copy, signed unified
   candidate, and exact outer hashes.
2. `installing`, `await-controller-logon` — receipt-owned installation, then
   sign out and sign in so the local controller-group SID is present in the new
   interactive token.
3. `await-hub-smoke` — manual Manager and Client launch, UI-only CMT1
   configuration, and the acknowledged Hub connection smoke gate.
4. `normal-e2e`, `await-normal-reboot`, `await-s1-resume`, `await-cold-start` —
   real-input E2E, then a manual reboot, a manual S1 sleep and resume, and a
   clean shutdown followed by a manual cold start, each with its own post-
   transition status and validation check.
5. `configure-verifier`, `await-verifier-reboot` — targeted
   `ComoteVirtualHidPhase2.sys` Driver Verifier in `oneboot` mode, another
   manual reboot, and real-input E2E under Verifier.
6. `cleanup-unified`, `await-post-verifier-clean-reboot` — exact receipt-owned
   uninstall, native `20/not-installed` proof, Verifier reset, signing-state
   cleanup audit, and a final clean reboot.
7. `export-role-candidate`, `await-manager-role-start` — atomic export of the
   Manager-only and Client+Broker+driver role ZIPs, then authentication and
   extraction of those bytes and a manual launch of the exported Manager role.
8. `install-client-role`, `await-client-role-logon`, `client-role-e2e`,
   `cleanup-client-role` — the exported Client role is installed on its own,
   requires another sign-out and sign-in, runs its own real-input E2E, and is
   then removed with proven-clean receipt-owned cleanup.
9. `final-report`, `promote-index`, `complete` — the pinned promotion evidence
   report and the bound promoted role index.

Plan for four manual reboots, one sleep/resume cycle, one cold start, and three
interactive logon transitions. This is not a single-sitting workflow.

## Installing in the disposable target VM

Copy the verified extracted bundle to a fixed local NTFS volume. Open an
elevated Windows PowerShell 5.1 window as the intended interactive local user.
Use the independently verified release-manifest hash:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Install-ComoteVirtualHidPreview.ps1 `
  -AcknowledgeDisposableVm `
  -ExpectedReleaseManifestSha256 <64-HEX> `
  -ControllerUser 'VMNAME\ExactInteractiveUser'
```

Before mutation, the installer verifies:

- exact VMware/Windows Home-family/x64/build 19045 identity;
- active kernel test-signing state, inactive HVCI, and disabled Secure Boot;
- the out-of-band release-manifest SHA-256;
- exact file and directory inventory, case, sizes, hashes, reparse ancestry,
  final paths, and one hard-link name per file where Windows exposes it;
- the strict driver manifest, exact INF/CAT/SYS, compiled native-installer pin,
  public certificate thumbprint, raw-certificate hash, code-signing EKU, and
  pinned Authenticode signers;
- a clean native `status --manifest` result (`20/not-installed`);
- absence of an unreceipted Broker service or protected install tree; and
- the explicitly named current interactive local user and SID.

If `Comote Input Controllers` already exists, preflight accepts only an empty
group or an exact singleton containing that ControllerUser SID. Authenticated
Users, arbitrary users, duplicate memberships, or any other SID fail before
mutation. The installer never preserves or adds such broader membership.

The first mutation is a protected wrapper receipt under ProgramData. It records
ownership intent before later mutations, allowing interrupted operations to
resume exact rollback. Application and maintenance files are copied below a
protected Program Files root. The Broker binary path is checked for
non-administrator write access before SCM creation.

The installer creates the local `Comote Input Controllers` group if necessary,
adds only the explicitly named interactive user if necessary, and does so
before the Broker starts. It imports only the exact pinned public certificate
to LocalMachine Root and TrustedPublisher, recording whether each copy was
new. It then invokes only this native CLI:

```text
install --package <exact-three-file-directory> --manifest <exact-file>
status --manifest <exact-file>
```

The Broker service is created as exact `ComoteInputBroker`, automatic,
LocalSystem, with no command-line arguments and a restrictive service DACL.
Every local process running as a user whose token contains the
`Comote Input Controllers` SID can use the Broker pipe. That account/session
is therefore an explicit trust boundary; the installer adds only the one exact
named local user and never Authenticated Users or arbitrary users.

Sign out and sign in after installation so the interactive token contains the
new group SID. From the verified bundle root, use the obvious `START HERE`
launchers. Each fails with exit code 1 when its protected installed executable
under `%ProgramFiles%\Comote\VirtualHidPreview` is absent:

- `START HERE - Manager Hub.cmd`: launches the protected Manager with exact
  arguments `--manager-hub`. Generate or enter the CMT1 access key in the
  Manager Hub UI; the application persists it with Windows DPAPI.
- `START HERE - Client Virtual HID.cmd`: launches the protected Client with
  exact arguments `--manager-hub --virtual-hid`. Saved secure configuration
  is protected with Windows DPAPI and reconnects automatically; the
  application opens setup when configuration is missing.

Keys are entered only through the UIs. Neither launcher places a key or
password on the command line, neither promotion evidence nor normal logs
record the key, and neither launcher enables `--allow-remote-tasks`.

## Uninstall and recovery

Run the uninstaller from the verified bundle or from the protected installed
`Maintenance` directory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Uninstall-ComoteVirtualHidPreview.ps1 `
  -AcknowledgeDisposableVm `
  -ExpectedReleaseManifestSha256 <64-HEX>
```

The uninstaller refuses to mutate anything without the exact protected
receipt. It audits every current file, service path/account/hash, group SID and
membership, and owned certificate copy before removal. It stops Broker,
invokes only `remove --manifest`, and requires a final native
`20/not-installed` status before deleting the service, group membership,
owned certificate copies, or maintenance inputs.

Native result `23` or `36` is a recovery boundary: the wrapper receipt,
certificate, service definition, native installer, driver manifest, and files
remain available. Reboot the VM if required and rerun the same uninstaller.
Never substitute wildcard/provider-based cleanup.

A pre-existing controller group is never deleted; only receipt-added
membership is removed. A wrapper-created group is deleted only if its exact
member audit proves no unowned member. A pre-existing pinned certificate copy
is never removed. The protected Broker log directory and OS logs (Windows
Event Log, SetupAPI, SCM, and other operating-system logs) are intentionally
left in place.

## Static host boundary

The only supported physical-host command is:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Test-VirtualHidPreviewBoundary.ps1
```

It checks PowerShell syntax and rejects broad recursive deletion,
TESTSIGNING/BCD mutation, unpinned certificate-store tooling, development
driver installers, firewall mutation, state overrides, access keys or
remote-task opt-in on launch command lines, and user-writable Broker service
paths. It performs no build, publish, signing, certificate, group, service,
device, input, or driver operation.

## Limitations and release gates

- The unified validation and promotion workflow is limited to a disposable
  VMware Windows 10 Home-family build 19045 x64 guest, any UBR, with test mode
  already active. Its driver, certificate, user-mode binaries, and installer
  are test-signed validation assets only.
- This is not a production, commercial, unattended, or general end-user
  deployment model. Production signing, distribution, update, rollback,
  support, and licensing gates remain separate work.
- No script changes the boot configuration. Preparing or restoring the VM's
  test-signing boot state remains a separate, explicit snapshot-controlled
  operation.
- Client launch is manual in a logged-on interactive user session. The preview
  does not perform unattended launch at the Windows login screen.
- Control of the secure desktop or UAC consent UI is not guaranteed, and
  Ctrl+Alt+Del is unavailable through this Virtual HID path.
- Text and IME input are translated as keyboard events. There is no arbitrary
  Unicode text-injection primitive.
- Sign-out and sign-in after installation is required so the new local-group
  SID appears in the interactive user's access token.
- Windows Event Log, SetupAPI/SCM/native OS logs, and protected Broker logs are
  retained for evidence and are not removed by receipt-owned uninstall.
- The pinned FFmpeg redistribution is an exact seven-library shared LGPL set.
  The release carries its LGPL license, notice, source-offer instructions,
  manifest, and independently pinned asset receipt both at bundle level and
  beside each application.
- `DOTNET_DIRECT_DEPENDENCIES.md` is supplemental human-readable context.
  Bundled `NUGET_SBOM.json` is the completed mandatory locked
  direct/transitive authority for versions, `contentHash`/`.nupkg.sha512`,
  used-by roles, licenses, HTTPS provenance, and zero-vulnerability scans.
  The automated evidence is still not legal advice.
