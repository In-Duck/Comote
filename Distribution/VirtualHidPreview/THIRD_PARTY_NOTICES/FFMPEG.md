# FFmpeg redistribution boundary

The Comote validation preview freezes exactly seven dynamically linked shared
libraries from the BtbN Windows LGPL build:

- build `n8.1.2-32-gcfa62de001-20260730`;
- architecture `x86_64`;
- native license expression `LGPL-3.0-or-later`;
- FFmpeg revision `cfa62de001af8ffeb7e22561f246469c7b809951`;
- BtbN build-scripts revision `a99e8230eae00d1cee38f23076a7a1f55cd984e2`;
- ABI majors avcodec/avdevice 62, avfilter 11, avformat 62, avutil 60,
  swresample 6, and swscale 9; and
- Windows Media Foundation `h264_mf` as the software H.264 fallback.

`FFMPEG_ASSET_RECEIPT.json` is schema 2 and mirrors the canonical
`ffmpeg/manifest.json` with the additional top-level `component: FFmpeg`
identity. It pins the immutable archive/checksum sources, source revisions,
managed wrappers, ABI/options, and the exact seven DLL byte lengths/SHA-256
values. GPL-only `libx264` and `libpostproc` are excluded.

Every applicable release location carries this exact six-file topology:

- `LICENSE.LGPLv3.txt`
- `LICENSE.SIPSorceryMedia.FFmpeg.LGPL-2.1.txt`
- `LICENSE.FFmpeg.AutoGen.MIT.txt`
- `NOTICE.md`
- `SOURCE_OFFER.md`
- `manifest.json`

There are two separate executable gates. Before any driver build, signing, or
host mutation, the isolated regression workflow publishes and runs an unsigned
self-contained MediaGate against synthetic frames and the external receipt.
The result is hash-bound in `Validation/REGRESSION_GATE.json`. The release
builder then publishes a fresh .NET 10.0.10 single-file
`Validation/Comote.MediaGate.exe`, Authenticode-signs it with the release
certificate, and pins its hash/signer/original filename in the
out-of-band-hash-bound release manifest `validationTools` entry. Promotion
reruns only that final pinned tool
and records new evidence before installation.

Normal Comote startup ignores external FFmpeg DLLs and extracts the bundled
libraries with exact hash verification. Advanced users may explicitly use an
ABI-compatible modified seven-library set only by combining an absolute local
`COMOTE_FFMPEG_DIR` with `--allow-modified-ffmpeg`. Network, relative,
reparse-point, missing, extra, or empty inputs are rejected. The user remains
responsible for replacement libraries and corresponding-source obligations.

This inventory, notice topology, and technical gate are not legal advice.
