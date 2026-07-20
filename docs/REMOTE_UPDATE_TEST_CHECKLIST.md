# Remote update test checklist

- Client version is lower than manifest version.
- Manifest and package URLs use HTTPS.
- ZIP SHA-256 matches the manifest.
- ZIP contains ComoteClient.exe and all required runtime files.
- Client downloads without Manager transferring the package.
- Client exits, replaces package files, and restarts with the original arguments.
- A bad SHA-256 leaves the current installation untouched.
- An unavailable manifest does not stop normal Client startup.
