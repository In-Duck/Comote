# Comote MediaGate

`Comote.MediaGate` is a release-promotion executable for an isolated Windows 10
VM. It does not capture the screen, open the network, install a driver, or alter
boot configuration.

The gate:

1. verifies the external FFmpeg asset receipt against its embedded canonical
   manifest;
2. extracts and hashes exactly seven embedded FFmpeg 8.1 LGPL shared libraries;
3. verifies the loaded ABI majors and FFmpeg build identity;
4. initialises `h264_mf` with the production options;
5. encodes deterministic synthetic BGRA frames and decodes the resulting H.264;
6. writes a JSON evidence file.

Required invocation:

```powershell
.\Comote.MediaGate.exe `
  --acknowledge-release-vm `
  --receipt "C:\absolute\path\FFMPEG_ASSET_RECEIPT.json" `
  --output "C:\absolute\path\media-gate-evidence.json"
```

The receipt and output paths must be absolute local paths. The output file must
not already exist. The gate only runs in a recognised x64 virtual machine on
Windows build `19045`.
