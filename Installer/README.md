# Comote small online installers

`ComoteClient_Setup.exe` and `ComoteManager_Setup.exe` are small Windows
bootstrap installers. They download the matching self-contained release ZIP
from the official `In-Duck/Comote` GitHub Release, verify its pinned SHA-256,
and then install it under `%ProgramFiles%\Comote`.

## Build

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Installer\Build_Web_Installers.ps1
```

Outputs:

- `artifacts/installers/ComoteClient_Setup.exe`
- `artifacts/installers/ComoteManager_Setup.exe`
- `artifacts/installers/web-installers.sha256.json`

The setup files only contain the installer UI and download logic. The large
.NET and FFmpeg runtime is downloaded during installation, so the setup files
remain small.

## Release checklist

Before publishing a new version:

1. Publish the Client and Manager ZIP assets.
2. Run `Build_Web_Installers.ps1 -UseLocalPackages` after building the ZIP
   files. The version, URLs, expected sizes, hashes, and manifest version are
   generated from the project and package outputs.
3. Build both installers and run their `/verify` self-check.
4. Upload the ZIP and setup files together to the same GitHub Release.

`release-config.json` pins the currently published packages for reproducible

The installer refuses to extract a package if its HTTPS URL, size limit, ZIP
paths, expected executable, or SHA-256 validation is invalid. Existing
installations are backed up during replacement and restored if installation
fails.

The setup executables are not currently Authenticode-signed. Windows
SmartScreen may therefore show an unknown-publisher warning until a code
signing certificate is added to the release process.
