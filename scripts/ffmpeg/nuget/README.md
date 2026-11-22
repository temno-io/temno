# Temno.FFmpeg.Runtime.win-x64

FFmpeg 9.1-temno1 for Windows x64: the build that [Temno](https://temno.io) ships, as a NuGet package, so
that a source build of Temno restores it like any other dependency. It is licensed GPL-3.0-or-later. It is not
an official FFmpeg build.

## What it holds

- `ffmpeg/`: `ffmpeg.exe`, `ffprobe.exe`, the `avcodec-63`, `avdevice-63`, `avfilter-12`, `avformat-63`,
  `avutil-61`, `swresample-7` and `swscale-10` DLLs, `LICENSE` (GPL version 3), `configure.txt` (the configure
  line) and `source.txt` (the version, source hash and build image). There is no `ffplay`.
- `build/Temno.FFmpeg.Runtime.win-x64.targets`: copies those files to an `ffmpeg` folder beside the
  referencing app, in its build output, publish folder and MSIX.
- `source/`: the recipe that rebuilds the files byte for byte.
- `THIRD-PARTY-NOTICES.md`: the libraries linked into the DLLs, their licences, and the notices they require.
- `FFMPEG-SOURCE-NOTICES.md`: the notices of FFmpeg's own BSD, MIT, ISC, zlib and IJG files that this build
  compiles, and those found in the linked libraries' and runtime's sources. The build file places it beside the app.

```xml
<PackageReference Include="Temno.FFmpeg.Runtime.win-x64" Version="[9.1.0-temno1]" />
```

Temno pins every file in `ffmpeg/` by SHA-256 (in `source/scripts/ffmpeg/Temno.FFmpeg.targets`) and keeps a
NuGet lock file, so its build refuses a package whose bytes or files differ.

## How it is built

- **Source:** the FFmpeg 9.1 release tarball, https://ffmpeg.org/releases/ffmpeg-9.1.tar.xz, SHA-256
  `1A4D394AE63D190A526CD052EB4415C156B5C5E2DF37EFAB41A7DE0B8FDE2433`, which is FFmpeg commit
  `74e4393595cf2b5857f0da925c4e0dd74ea306e5`, built as published, with no patches.
- **Recipe:** `source/scripts/ffmpeg/build-in-container.sh` holds the configure line and build steps.
  `build-ffmpeg.ps1` runs it in [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)' `win64-gpl-shared`
  image, pulled by digest `sha256:82b19b2dda7139d96228fcd56bf33c050764bd9e4ca3ae29eb3c50a648bcb714` from
  `ghcr.io/btbn/ffmpeg-builds/win64-gpl-shared`, or from Temno's copy at
  `ghcr.io/temno-io/btbn-ffmpeg-builds-win64-gpl-shared` with the same digest.
- **Configuration:** `--enable-gpl` and `--enable-version3`, never `--enable-nonfree`. Networking is disabled,
  and only the `file` and `pipe` protocols are enabled. The `dash`, `imf`, `hls`, `rtsp`, `sdp`, `rtp`, `sap`,
  `cdxl`, `pcx_pipe` and `sunrast_pipe` demuxers and the `cdxl`, `g2m`, `pcx` and `sunrast` decoders are left
  out. It links x264, x265, zimg, dav1d, zlib, liblzma and the libvpl dispatcher, and the NVENC, AMF and QSV
  interfaces.

## Corresponding source

- FFmpeg: the release tarball above, with the build scripts in `source/`.
- The libraries compiled into the DLLs: the build image holds them already compiled, not their source. Each
  library's source is its upstream repository at the commit `THIRD-PARTY-NOTICES.md` names, and the matching
  script in `scripts.d` of [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) at commit
  `5ece62897c650b863c1ddda7b6964845344aef2d` shows where it was fetched from and how it was changed and built.

## Rebuild it and compare

The build is reproducible. Copy `source/` to a folder of your own and open a terminal there. With PowerShell 7,
Docker and Linux containers, on Windows or Linux:

```powershell
pwsh scripts/ffmpeg/build-ffmpeg.ps1
```

It downloads the tarball, checks its hash, builds into `tools/ffmpeg/build/9.1-temno1`, and fails unless the
two executables, the seven DLLs and `LICENSE` equal their pins. The build also lists the files it compiled
(`compiled-sources.txt`), from which `scripts/ffmpeg/source-notices.ps1 -Rid win-x64` writes
`FFMPEG-SOURCE-NOTICES.md`. On Windows,
`pwsh scripts/ensure-ffmpeg.ps1 -FromSource` also writes `configure.txt` and `source.txt` from the binaries and
the pins, checks all twelve files, and runs the binaries to check the configuration. When this package was
made, rebuilds on Windows and on Linux matched the pins.

FFmpeg is a trademark of Fabrice Bellard.
