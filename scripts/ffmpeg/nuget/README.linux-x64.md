# Temno.FFmpeg.Runtime.linux-x64

FFmpeg 9.1-temno1 for Linux x64: the build that [Temno](https://temno.io)'s hosted services run, as a NuGet
package. It is the Linux counterpart of `Temno.FFmpeg.Runtime.win-x64`, from the same source and configuration.
It is licensed GPL-3.0-or-later. It is not an official FFmpeg build.

## What it holds

- `ffmpeg/bin/`: `ffmpeg` and `ffprobe`. They find their libraries through the run path `$ORIGIN/../lib`.
- `ffmpeg/lib/`: `libavcodec.so.63`, `libavdevice.so.63`, `libavfilter.so.12`, `libavformat.so.63`,
  `libavutil.so.61`, `libswresample.so.7` and `libswscale.so.10`, each under the name the binaries load it by.
- `ffmpeg/LICENSE` (GPL version 3), `ffmpeg/configure.txt` (the version and configure line) and
  `ffmpeg/source.txt` (the source hash and build image). There is no `ffplay`.
- `source/`: the recipe that rebuilds the files byte for byte.
- `THIRD-PARTY-NOTICES.md`: the libraries linked into the shared libraries, their licences, and the notices they
  require.
- `FFMPEG-SOURCE-NOTICES.md`: the notices of FFmpeg's own files that carry BSD, MIT, ISC, zlib or IJG terms, for
  each such file this build compiled, copied from the FFmpeg source.

A zip archive keeps no Unix file modes: after extracting, mark `ffmpeg/bin/ffmpeg` and `ffmpeg/bin/ffprobe`
executable. The files need the system's C library (glibc 2.28 or later) and nothing else outside the package.
Temno pins every file by SHA-256 in `source/scripts/ffmpeg/Temno.FFmpeg.linux-x64.targets`.

## How it is built

- **Source:** the FFmpeg 9.1 release tarball, https://ffmpeg.org/releases/ffmpeg-9.1.tar.xz, SHA-256
  `1A4D394AE63D190A526CD052EB4415C156B5C5E2DF37EFAB41A7DE0B8FDE2433`, which is FFmpeg commit
  `74e4393595cf2b5857f0da925c4e0dd74ea306e5`, built as published, with no patches.
- **Recipe:** `source/scripts/ffmpeg/build-in-container-linux.sh` holds the configure line, the build steps and
  the checks. `build-ffmpeg.ps1 -Rid linux-x64` runs it in [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)'
  `linux64-gpl-shared` image, pulled by digest from `ghcr.io/btbn/ffmpeg-builds/linux64-gpl-shared`, or from
  Temno's copy at `ghcr.io/temno-io/btbn-ffmpeg-builds-linux64-gpl-shared` with the same digest (in the targets
  file above).
- **Configuration:** `--enable-gpl` and `--enable-version3`, never `--enable-nonfree`. Networking is disabled,
  and only the `file` and `pipe` protocols are enabled. The `dash`, `imf`, `hls`, `rtsp`, `sdp`, `rtp`, `sap`,
  `cdxl`, `pcx_pipe` and `sunrast_pipe` demuxers and the `cdxl`, `g2m`, `pcx` and `sunrast` decoders are left
  out. It links x264, x265, zimg, dav1d, zlib and liblzma, and libgcc and libstdc++ statically. Unlike the
  Windows build it has no hardware interfaces (D3D, DXVA2, AMF, QSV, NVENC).

## Corresponding source

- FFmpeg: the release tarball above, with the build scripts in `source/`.
- The libraries compiled into the shared libraries: the build image holds them already compiled, not their
  source. Each library's source is its upstream repository at the commit `THIRD-PARTY-NOTICES.md` names, and the
  matching script in `scripts.d` of [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) shows where it
  was fetched from and how it was changed and built.

## Rebuild it and compare

The build is reproducible. Copy `source/` to a folder of your own and open a terminal there. With PowerShell 7,
Docker and Linux containers, on Windows or Linux:

```powershell
pwsh scripts/ffmpeg/build-ffmpeg.ps1 -Rid linux-x64
```

It downloads the tarball, checks its hash, builds into `tools/ffmpeg/build/9.1-temno1-linux-x64`, runs the
binaries in the build image to check the configuration, and fails unless every file equals its pin.

FFmpeg is a trademark of Fabrice Bellard.
