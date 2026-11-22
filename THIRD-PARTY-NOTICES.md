# Third-party notices

Temno for Windows is licensed under the GNU General Public License, version 3 (see `LICENSE` and `NOTICE`).
The components below are bundled in the MSIX. Their license terms are unchanged.

## FFmpeg 9.1-temno1

A GPL shared Windows x64 build of FFmpeg that Temno makes itself
([ADR-010](docs/decisions/ADR-010-ffmpeg-built-from-source.md)). Its corresponding source is:

- the [FFmpeg 9.1 release tarball](https://ffmpeg.org/releases/ffmpeg-9.1.tar.xz), SHA-256
  `1A4D394AE63D190A526CD052EB4415C156B5C5E2DF37EFAB41A7DE0B8FDE2433`, which is
  [FFmpeg commit 74e4393595cf2b5857f0da925c4e0dd74ea306e5](https://github.com/FFmpeg/FFmpeg/commit/74e4393595cf2b5857f0da925c4e0dd74ea306e5),
  built as published, with no patches;
- `scripts/ffmpeg/build-in-container.sh`, the configure line and build steps, run by
  `scripts/ffmpeg/build-ffmpeg.ps1` in the
  [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) image
  `ghcr.io/btbn/ffmpeg-builds/win64-gpl-shared@sha256:82b19b2dda7139d96228fcd56bf33c050764bd9e4ca3ae29eb3c50a648bcb714`,
  built from that repository's commit 5ece62897c650b863c1ddda7b6964845344aef2d. Temno keeps a copy with
  the same digest at `ghcr.io/temno-io/btbn-ffmpeg-builds-win64-gpl-shared`. The image holds the libraries
  below already compiled, not their source: each library's source is its upstream repository at the
  commit named below, and the matching script in `scripts.d` of BtbN/FFmpeg-Builds at that commit shows
  where it was fetched from and how it was changed and built.

The configure line includes `--enable-gpl` and `--enable-version3`, and does not include
`--enable-nonfree`. Networking is disabled, and only the `file` and `pipe` protocols are enabled. The
`dash`, `imf`, `hls`, `rtsp`, `sdp`, `rtp`, `sap`, `cdxl`, `pcx_pipe` and `sunrast_pipe` demuxers and the
`cdxl`, `g2m`, `pcx` and `sunrast` decoders are left out. The x264 it links keeps x264's OpenCL lookahead, which
loads `OpenCL.dll` at run time only when a caller turns on x264's `opencl` option. These libraries are linked into
the FFmpeg DLLs; they are not separate files:

- x264 (commit 0480cb05) — GPL-2.0-or-later, Copyright (C) 2003-2025 x264 project; `encoder/lookahead.c` also
  Copyright (C) 2010-2025 Avail Media and x264 project.
- The OpenCL headers x264 carries in `extras/` (`cl.h`, `cl_platform.h`), compiled in with its OpenCL lookahead —
  the Khronos license (MIT-style), Copyright (c) 2008 - 2012 The Khronos Group Inc. and Copyright (c) 2008-2012
  The Khronos Group Inc. The license text is in `FFMPEG-SOURCE-NOTICES.md`.
- x265 (commit 5143a2a0) — GPL-2.0-or-later, Copyright (C) 2013-2025 MulticoreWare, Inc; portions Copyright (C)
  2003-2013 x264 project.
- json11, which x265 carries in `source/dynamicHDR10/json11` and builds for HDR10+ — MIT, Copyright (c) 2013
  Dropbox, Inc. Its license text is in the MIT license section below.
- `x86inc.asm`, the assembly layer that x264, x265 and dav1d each carry — ISC. x264's copy: Copyright (C)
  2005-2025 x264 project. x265's: Copyright (C) 2003-2013 x264 project, Copyright (C) 2013-2020 MulticoreWarae, Inc
  (so spelled). dav1d's: Copyright (C) 2005-2024 x264 project. The license text is in the ISC license section below.
- dav1d (commit b8b2d726) — BSD-2-Clause, Copyright © 2018-2025, VideoLAN and dav1d authors. Its files also
  name Two Orioles, LLC, VideoLabs, Niklas Haas, The rav1e contributors, Nathan Egge and Matthias Dressel; every
  copyright line of the files this build compiles is in `FFMPEG-SOURCE-NOTICES.md`, with dav1d's patent license,
  the Alliance for Open Media Patent License 1.0. Its license text is below.
- zimg (commit 67e06032) and its graphengine submodule (github.com/sekrit-twc/graphengine, commit cb5b2ce1) —
  WTFPL, Version 2.
- zlib (commit 9d314c34) — zlib license, (C) 1995-2026 Jean-loup Gailly and Mark Adler.
- liblzma from XZ Utils (commit 299eb5c3) — BSD Zero Clause License.
- Intel libvpl (commit 674d015b), its dispatcher and API headers — MIT, Copyright (c) 2020 Intel Corporation; the
  files' own lines are in `FFMPEG-SOURCE-NOTICES.md`.
- AMD AMF headers (commit 57ee0cfb) — MIT, Copyright (c) 2017-2026 Advanced Micro Devices, Inc.; each compiled
  header's line, and AMD's notice about standards, are in `FFMPEG-SOURCE-NOTICES.md`.
- NVIDIA codec headers from nv-codec-headers (commit f2053071) — MIT, Copyright (c) 2010-2026
  NVIDIA Corporation; the lines as written are in `FFMPEG-SOURCE-NOTICES.md`.
- The mingw-w64 runtime and its winpthreads library (commit 7213d719) — the Zope Public License 2.1,
  and MIT with a BSD-style notice for winpthreads. Both license texts are below. The runtime parts linked in also
  hold David M. Gay's gdtoa (Lucent Technologies' notice) and a BSD `strtok_r` (Softweyr LLC and the Regents of the
  University of California); their notices, and winpthreads' copyright lines, are in `FFMPEG-SOURCE-NOTICES.md`.
- libgcc and libstdc++ from GCC 16.2.0 — GPL-3.0 with the GCC Runtime Library Exception. libstdc++'s headers keep
  Hewlett-Packard's and Silicon Graphics' permission notices, which are in `FFMPEG-SOURCE-NOTICES.md`.

Some of FFmpeg's own source files carry BSD, MIT, ISC, zlib or IJG terms of their own (FFmpeg's `LICENSE.md`
calls them MIT/X11/BSD-style). `FFMPEG-SOURCE-NOTICES.md`, beside this file, holds the notice of each such file this
build compiled, copied from the FFmpeg source by `scripts/ffmpeg/source-notices.ps1`, and the notices found in the
linked libraries' and runtime's sources at the commits above.

Shipped beside the app in `ffmpeg/`:

- `ffmpeg.exe`, `ffprobe.exe`, and the `avcodec`, `avdevice`, `avfilter`, `avformat`, `avutil`,
  `swresample` and `swscale` DLLs
- `LICENSE` — GNU General Public License, version 3
- `configure.txt` — the configure line
- `source.txt` — the version, source hash and build image

and, beside the app, `FFMPEG-SOURCE-NOTICES.md`.

FFmpeg's `libavcodec/jfdctfst.c`, `libavcodec/jfdctint_template.c` and `libavcodec/jrevdct.c` come from
libjpeg: this software is based in part on the work of the Independent JPEG Group. Temno does not change
those files.

`ffplay` is not shipped. A build of Temno from source receives the same files from the NuGet package
`Temno.FFmpeg.Runtime.win-x64` 9.1.0-temno1, which also carries these notices, `FFMPEG-SOURCE-NOTICES.md` and, in
its `source/` folder, the recipe above. FFmpeg is a trademark of Fabrice Bellard.

dav1d's license:

Copyright © 2018-2025, VideoLAN and dav1d authors
All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions
   and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials provided
   with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF
THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

mingw-w64's license (`COPYING` at mingw-w64 commit 7213d719fe344553aa24fef7cc7dedee4940d594):

```text
With exception of certain parts that are prominently marked as being
in the Public Domain, BSD, or LGPL this Software is provided under the
Zope Public License (ZPL) Version 2.1.

Copyright (c) 2009 - 2013 by the mingw-w64 project

See the AUTHORS file for the list of contributors to the mingw-w64 project.

This license has been certified as open source. It has also been designated
as GPL compatible by the Free Software Foundation (FSF).

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

   1. Redistributions in source code must retain the accompanying copyright
      notice, this list of conditions, and the following disclaimer.
   2. Redistributions in binary form must reproduce the accompanying
      copyright notice, this list of conditions, and the following disclaimer
      in the documentation and/or other materials provided with the
      distribution.
   3. Names of the copyright holders must not be used to endorse or promote
      products derived from this software without prior written permission
      from the copyright holders.
   4. The right to distribute this software or to use it for any purpose does
      not give you the right to use Servicemarks (sm) or Trademarks (tm) of
      the copyright holders.  Use of them is covered by separate agreement
      with the copyright holders.
   5. If any files are modified, you must cause the modified files to carry
      prominent notices stating that you changed the files and the date of
      any change.

Disclaimer

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ``AS IS'' AND ANY EXPRESSED
OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO
EVENT SHALL THE COPYRIGHT HOLDERS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, 
OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE,
EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

winpthreads' license (`mingw-w64-libraries/winpthreads/COPYING` at the same commit):

```text
Copyright (c) 2011 mingw-w64 project

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.


/*
 * Parts of this library are derived by:
 *
 * Posix Threads library for Microsoft Windows
 *
 * Use at own risk, there is no implied warranty to this code.
 * It uses undocumented features of Microsoft Windows that can change
 * at any time in the future.
 *
 * (C) 2010 Lockless Inc.
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without modification,
 * are permitted provided that the following conditions are met:
 *
 *
 *  * Redistributions of source code must retain the above copyright notice,
 *    this list of conditions and the following disclaimer.
 *  * Redistributions in binary form must reproduce the above copyright notice,
 *    this list of conditions and the following disclaimer in the documentation
 *    and/or other materials provided with the distribution.
 *  * Neither the name of Lockless Inc. nor the names of its contributors may be
 *    used to endorse or promote products derived from this software without
 *    specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AN
 * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
 * IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
 * INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
 * DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
 * LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE
 * OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
 * OF THE POSSIBILITY OF SUCH DAMAGE.
 */
```

## FFmpeg 9.1-temno1 for Linux x64

A GPL shared Linux x64 build of FFmpeg that Temno makes itself for its hosted services, from the same source and
configuration as Temno's Windows build (`Temno.FFmpeg.Runtime.win-x64`). Its corresponding source is:

- the [FFmpeg 9.1 release tarball](https://ffmpeg.org/releases/ffmpeg-9.1.tar.xz), SHA-256
  `1A4D394AE63D190A526CD052EB4415C156B5C5E2DF37EFAB41A7DE0B8FDE2433`, which is
  [FFmpeg commit 74e4393595cf2b5857f0da925c4e0dd74ea306e5](https://github.com/FFmpeg/FFmpeg/commit/74e4393595cf2b5857f0da925c4e0dd74ea306e5),
  built as published, with no patches;
- `scripts/ffmpeg/build-in-container-linux.sh`, the configure line, build steps and checks, run by
  `scripts/ffmpeg/build-ffmpeg.ps1 -Rid linux-x64` in the
  [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) image
  `ghcr.io/btbn/ffmpeg-builds/linux64-gpl-shared@sha256:274242a06b91ff977e9d16b288c3b32cf8dc5bd47bf3e8ca898a6ed486d986f8`.
  Temno keeps a copy with the same digest at `ghcr.io/temno-io/btbn-ffmpeg-builds-linux64-gpl-shared`. The image
  holds the libraries below already compiled, not their source: each library's source is its upstream repository
  at the commit named below, and the matching script in `scripts.d` of BtbN/FFmpeg-Builds shows where it was
  fetched from and how it was changed and built. The libraries' versions in this image, read from its pkg-config
  files, equal those in the Windows image; BtbN's scripts pin the commits below at
  5ece62897c650b863c1ddda7b6964845344aef2d, and the one later commit, c63bb308a1de, changed SVT-AV1 alone.

The configure line includes `--enable-gpl` and `--enable-version3`, and does not include
`--enable-nonfree`. Networking is disabled, and only the `file` and `pipe` protocols are enabled. The
`dash`, `imf`, `hls`, `rtsp`, `sdp`, `rtp`, `sap`, `cdxl`, `pcx_pipe` and `sunrast_pipe` demuxers and the
`cdxl`, `g2m`, `pcx` and `sunrast` decoders are left out. FFmpeg is configured with no hardware acceleration
(libavdevice keeps Linux's V4L2 capture and output devices); the x264 it links keeps x264's OpenCL lookahead, which
loads `libOpenCL.so` at run time only when a caller turns on x264's `opencl` option. These libraries are linked into the FFmpeg shared libraries; they are not separate files:

- x264 (commit 0480cb05) — GPL-2.0-or-later, Copyright (C) 2003-2025 x264 project; `encoder/lookahead.c` also
  Copyright (C) 2010-2025 Avail Media and x264 project.
- The OpenCL headers x264 carries in `extras/` (`cl.h`, `cl_platform.h`), compiled in with its OpenCL lookahead —
  the Khronos license (MIT-style), Copyright (c) 2008 - 2012 The Khronos Group Inc. and Copyright (c) 2008-2012
  The Khronos Group Inc. The license text is in `FFMPEG-SOURCE-NOTICES.md`.
- x265 (commit 5143a2a0) — GPL-2.0-or-later, Copyright (C) 2013-2025 MulticoreWare, Inc; portions Copyright (C)
  2003-2013 x264 project.
- json11, which x265 carries in `source/dynamicHDR10/json11` and builds for HDR10+ — MIT, Copyright (c) 2013
  Dropbox, Inc. Its license text is in the MIT license section below.
- `x86inc.asm`, the assembly layer that x264, x265 and dav1d each carry — ISC. x264's copy: Copyright (C)
  2005-2025 x264 project. x265's: Copyright (C) 2003-2013 x264 project, Copyright (C) 2013-2020 MulticoreWarae, Inc
  (so spelled). dav1d's: Copyright (C) 2005-2024 x264 project. The license text is in the ISC license section below.
- dav1d (commit b8b2d726) — BSD-2-Clause, Copyright © 2018-2025, VideoLAN and dav1d authors. Its files also
  name Two Orioles, LLC, VideoLabs, Niklas Haas, The rav1e contributors, Nathan Egge and Matthias Dressel; every
  copyright line of the files this build compiles is in `FFMPEG-SOURCE-NOTICES.md`, with dav1d's patent license,
  the Alliance for Open Media Patent License 1.0. Its license text is below.
- zimg (commit 67e06032) and its graphengine submodule (github.com/sekrit-twc/graphengine, commit cb5b2ce1) —
  WTFPL, Version 2.
- zlib (commit 9d314c34) — zlib license, (C) 1995-2026 Jean-loup Gailly and Mark Adler.
- liblzma from XZ Utils (commit 299eb5c3) — BSD Zero Clause License.
- libgcc and libstdc++ from GCC 16.2.0 — GPL-3.0 with the GCC Runtime Library Exception. libstdc++'s headers keep
  Hewlett-Packard's and Silicon Graphics' permission notices, which are in `FFMPEG-SOURCE-NOTICES.md`.

The files use the system's GNU C Library (glibc 2.28 or later), licensed under the GNU Lesser General Public License,
version 2.1 or later, whose text is in `FFMPEG-SOURCE-NOTICES.md`. A few small parts of glibc 2.28 are
linked into the files themselves: its startup objects (`crti.o` and `crtn.o` in every file; `Scrt1.o` and
`libc_nonshared.a`'s `elf-init` in `ffmpeg` and `ffprobe`), which carry glibc's permission to link them into other
programs and distribute those without restriction, except `csu/init.c`'s one constant (`_IO_stdin_used`); and
`libmvec_nonshared.a`'s `svml_finite_alias` in `libavcodec`, symbol aliases into glibc's vector maths library, under
the LGPL alone.

Some of FFmpeg's own source files carry BSD, MIT, ISC, zlib or IJG terms of their own (FFmpeg's `LICENSE.md`
calls them MIT/X11/BSD-style). `FFMPEG-SOURCE-NOTICES.md` in the package holds the notice of each such file this
build compiled, copied from the FFmpeg source by `scripts/ffmpeg/source-notices.ps1`, and the notices found the
same way in the linked libraries' sources at the commits above.

Shipped in `ffmpeg/`:

- `bin/ffmpeg`, `bin/ffprobe`, and the `libavcodec`, `libavdevice`, `libavfilter`, `libavformat`, `libavutil`,
  `libswresample` and `libswscale` shared libraries in `lib/`
- `LICENSE` — GNU General Public License, version 3
- `configure.txt` — the version and configure line
- `source.txt` — the version, source hash and build image

FFmpeg's `libavcodec/jfdctfst.c`, `libavcodec/jfdctint_template.c` and `libavcodec/jrevdct.c` come from
libjpeg: this software is based in part on the work of the Independent JPEG Group. Temno does not change
those files.

`ffplay` is not shipped. The NuGet package `Temno.FFmpeg.Runtime.linux-x64` 9.1.0-temno1 carries these files, these
notices, `FFMPEG-SOURCE-NOTICES.md` and, in its `source/` folder, the recipe above; Temno's Gateway image takes the
files from it. FFmpeg is a trademark of Fabrice Bellard.

dav1d's license:

Copyright © 2018-2025, VideoLAN and dav1d authors
All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions
   and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials provided
   with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF
THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Whisper

- Whisper.net 1.8.1 and Whisper.net.Runtime 1.8.1 — MIT, Copyright (c) 2024 sandrohanea.
  The runtime contains whisper.cpp's native library (MIT, Georgi Gerganov,
  https://github.com/ggerganov/whisper.cpp).
- `whisper/ggml-base.en.bin` — the OpenAI Whisper `base.en` weights, MIT, Copyright OpenAI.
  Fetched from https://huggingface.co/ggerganov/whisper.cpp. SHA-256
  `A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002`.

## ONNX Runtime

Microsoft.ML.OnnxRuntime 1.30.0 — MIT, Copyright (c) Microsoft Corporation.
Used for visual search. The duplicate Windows App SDK ONNX runtime is not shipped.

## Audio and imaging

- NAudio 2.2.1 — MIT, Copyright 2020 Mark Heath.
- System.Drawing.Common 10.0.14 — MIT, Copyright (c) .NET Foundation and Contributors.

## DirectX bindings

Vortice.Windows 3.8.3 (`Vortice.Direct3D11`, `Vortice.Direct2D1`, `Vortice.D3DCompiler`, and the
DirectX bindings they bring in) — MIT, Copyright (c) Amer Koleci and contributors.
https://github.com/amerkoleci/Vortice.Windows

## Visual C++ runtime

The MSIX carries the Microsoft Visual C++ 2015–2022 app-local runtime next to the app:
`vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll`, `msvcp140_1.dll`, and `vcomp140.dll`.
ONNX Runtime and the Whisper native libraries import these. They are copied from the build
machine's Windows system directory, which is the installed Visual C++ redistributable.
Microsoft's license terms for that redistributable apply. They are not the GPL.

## Windows App SDK

Microsoft.WindowsAppSDK 2.4.0. The MSIX redistributes the Windows App SDK runtime under the
Microsoft Software License Terms for the Windows App SDK. Those terms are not the GPL.

## Other .NET packages

System.Security.Cryptography.ProtectedData 10.0.14 — MIT, Copyright (c) .NET Foundation and Contributors.

## .NET runtime

The desktop app is published self-contained, so the MSIX carries the .NET 10 runtime and the Windows Forms
part of the .NET 10 Windows Desktop runtime beside the app; nobody installs .NET separately. The patch release is the
one the build's .NET SDK provides (10.0.14 in WP-404's development package). MIT, Copyright (c) .NET
Foundation and Contributors. The runtime's own third-party notices are
https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT and the matching files in
https://github.com/dotnet/wpf and https://github.com/dotnet/winforms.

## Fonts

Both fonts are shipped under the SIL Open Font License 1.1. The license text is beside each font.

- Inter — Copyright 2020 The Inter Project Authors. `Assets/Fonts/Inter.ttf`, `Assets/Fonts/Inter-OFL.txt`.
- Roboto Mono — Copyright 2015 The Roboto Mono Project Authors. `Assets/Fonts/RobotoMono.ttf`,
  `Assets/Fonts/RobotoMono-OFL.txt`.

## MIT license

The MIT components above are licensed as follows, with the copyright line of that component:

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## ISC license

The ISC components above are licensed as follows, with the copyright lines of that component:

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
