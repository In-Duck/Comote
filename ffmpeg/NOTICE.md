# FFmpeg distribution notice

Comote includes dynamically linked FFmpeg shared libraries from the BtbN
Windows LGPL build:

- Build: `n8.1.2-32-gcfa62de001-20260730`
- FFmpeg revision: `cfa62de001af8ffeb7e22561f246469c7b809951`
- BtbN build-scripts revision: `a99e8230eae00d1cee38f23076a7a1f55cd984e2`
- Native-library license: GNU Lesser General Public License, version 3
- Required ABI: FFmpeg 8.1 (`avcodec` 62 and matching library majors)

The bundled build excludes `libx264` and `libpostproc`. The software H.264
fallback uses Windows Media Foundation (`h264_mf`).

Comote also distributes `SIPSorceryMedia.FFmpeg` 10.0.12 under
LGPL-2.1-only and `FFmpeg.AutoGen` 8.1.0 under the MIT License. Their license
texts and exact source revisions are included alongside this notice.

The exact immutable archive URL, archive hash, individual library hashes, ABI
majors, and managed-wrapper revisions are in `manifest.json`. Applicable
license texts are in this directory, and corresponding-source locations are in
`SOURCE_OFFER.md`.

## Replacing the shared libraries

Normal Comote startup ignores external FFmpeg DLLs and extracts the bundled
libraries with exact hash verification. Advanced users may explicitly use ABI-
compatible modified FFmpeg libraries for relinking and debugging:

1. Put all seven DLLs listed in `manifest.json` in one absolute local directory.
2. Set `COMOTE_FFMPEG_DIR` to that directory.
3. Start Comote with the `--allow-modified-ffmpeg` argument.

Both the environment variable and command-line argument are required. Network
paths, relative paths, reparse-point paths, missing files, and empty files are
rejected. Modified libraries must retain the required ABI majors and exported
interfaces. When the explicit override is enabled, the user accepts
responsibility for the replacement libraries.

FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project.
Comote and BtbN are not affiliated with the FFmpeg project.
