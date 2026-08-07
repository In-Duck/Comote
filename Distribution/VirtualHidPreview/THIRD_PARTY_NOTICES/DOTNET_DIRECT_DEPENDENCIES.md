# Direct .NET dependency notices

This notice covers the direct NuGet dependencies declared by the Comote Client
(`Host`), Manager (`Viewer`), and Input Broker projects. `NUGET_SBOM.json` is
the mandatory resolved authority for exact direct/transitive versions, lock
`contentHash`, matching `.nupkg.sha512` values, used-by roles, licenses,
provenance URLs, and package-metadata hashes. The source-inventory and outer
package hashes bind that SBOM to the release.

The release gate accepts the package only after every release/test project
restores from a source-inventoried `packages.lock.json`, the machine-readable
vulnerability scans report zero direct or transitive vulnerable packages, and
the resolved SBOM passes license/provenance review. This direct list remains a
human-readable minimum notice and is not legal advice.

| Package | Version | Declared license | Project/source |
|---|---:|---|---|
| Concentus | 2.2.2 | bundled BSD-style/Opus license | https://github.com/lostromb/concentus |
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.10 | MIT | https://dot.net/ |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.10 | MIT | https://dot.net/ |
| Microsoft.Extensions.Logging.Abstractions | 10.0.10 | MIT | https://dot.net/ |
| NAudio | 2.2.1 | MIT | https://github.com/naudio/NAudio |
| PusherClient | 2.3.0-beta | MIT | https://github.com/pusher/pusher-websocket-dotnet |
| PusherServer | 5.0.0 | MIT | https://github.com/pusher/pusher-http-dotnet |
| Vortice.Direct3D11 | 3.6.2 | MIT | https://github.com/amerkoleci/Vortice.Windows |
| Vortice.DXGI | 3.6.2 | MIT | https://github.com/amerkoleci/Vortice.Windows |
| Vortice.Mathematics | 2.0.0 | MIT | https://github.com/amerkoleci/Vortice.Windows |
| SIPSorcery | 10.0.12 | BSD-3-Clause | https://github.com/sipsorcery-org/sipsorcery |
| SIPSorceryMedia.FFmpeg | 10.0.12 | LGPL-2.1-only | https://github.com/sipsorcery-org/sipsorcery/tree/master/src/SIPSorceryMedia.FFmpeg |
| Newtonsoft.Json | 13.0.3 | MIT | https://github.com/JamesNK/Newtonsoft.Json |
| Supabase | 1.1.1 | MIT | https://github.com/supabase-community/supabase-csharp |
| Supabase.Postgrest | 4.1.0 | MIT | https://github.com/supabase-community/postgrest-csharp |

`InputCore` and `InputBroker` declare no direct third-party NuGet packages; the
Broker references `InputCore`.

## Concentus/Opus notice

Copyright is held by various parties, including Skype Limited, Xiph.Org
Foundation, CSIRO, Microsoft Corporation, Jean-Marc Valin, Gregory Maxwell,
Mark Borgerding, Timothy B. Terriberry, and Logan Stromberg.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that source redistributions retain the
copyright notice, conditions, and disclaimer; binary redistributions reproduce
them in documentation/materials; and contributor names are not used to endorse
derived products without permission.

The software is provided "as is," without express or implied warranties; the
copyright holders and contributors are not liable for damages arising from its
use. Concentus 2.2.2 identifies the corresponding Opus source at:
https://gitlab.xiph.org/xiph/opus/-/tags/v1.5.2

## MIT-licensed packages

The MIT-licensed packages above permit use, copying, modification, merging,
publication, distribution, sublicensing, and sale, provided the applicable
copyright and permission notices are included. They are provided without
warranty. Refer to each linked project and the exact NuGet package for its
copyright holders and complete license text.

## BSD-3-Clause package

SIPSorcery redistribution must retain its copyright notice, conditions, and
disclaimer, and must not use copyright-holder/contributor names to endorse
derived products without permission. It is provided without warranty.

## LGPL wrapper

`SIPSorceryMedia.FFmpeg` declares LGPL-2.1-only. This notice does not determine
the independent obligations of the native FFmpeg DLLs embedded by Comote;
those exact assets have a separate pinned FFmpeg notice and receipt.
