Client update publishing checklist

1. Build the complete ComoteClient win-x64 package.
2. Zip ComoteClient.exe, DLL files, runtimes, and resources together.
3. Calculate SHA-256 for the ZIP.
4. Upload the ZIP to GitHub Releases or another HTTPS host.
5. Publish a manifest based on client-update.example.json.
6. Send action=update and the manifest HTTPS URL from Manager Hub.
