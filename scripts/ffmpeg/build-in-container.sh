#!/bin/bash
# Builds the FFmpeg that Temno ships (WP-401). build-ffmpeg.ps1 runs this inside
# ghcr.io/btbn/ffmpeg-builds/win64-gpl-shared, pinned by digest, which provides the mingw-w64
# cross toolchain and the static libraries named below. Inputs are staged in /work; the
# runtime files land in /out.
#
# Source: the signed FFmpeg 9.1 release tarball, plus any upstream fixes in /work/patches. 9.1
# carries the six that 9.0.2-temno1 added (WP-442), so there are none today.
# Excluded: every network protocol and network demuxer, and the demuxers and decoders named
# by the advisories in docs/verification/wp-321/native-disposition.md that Temno's supported
# imports do not need.
set -euo pipefail

release=9.1
# ld and strip write this into every PE header instead of the wall clock, so a rebuild is
# byte-identical. It is the 9.1 release commit's time (74e43935, 2026-11-19T18:47:40Z).
export SOURCE_DATE_EPOCH=1795114060

cd /work
rm -rf "ffmpeg-$release" prefix
tar -xJf "ffmpeg-$release.tar.xz"
cd "ffmpeg-$release"

shopt -s nullglob
for patch in /work/patches/*.patch; do
    echo "Applying $(basename "$patch")"
    git apply --verbose "$patch"
done
shopt -u nullglob

./configure \
    --prefix=/work/prefix \
    --pkg-config-flags="--static" \
    $FFBUILD_TARGET_FLAGS \
    --cc="$CC" --cxx="$CXX" --ar="$AR" --ranlib="$RANLIB" --nm="$NM" \
    --extra-ldflags="-pthread -Wl,--no-insert-timestamp" \
    --extra-version=temno1 \
    --enable-gpl --enable-version3 \
    --enable-shared --disable-static \
    --disable-debug --disable-doc --disable-ffplay \
    --disable-autodetect \
    --disable-w32threads --enable-pthreads \
    --enable-zlib --enable-lzma \
    --enable-libx264 --enable-libx265 --enable-libzimg --enable-libdav1d \
    --enable-d3d11va --enable-d3d12va --enable-dxva2 \
    --enable-ffnvcodec --enable-cuda --enable-cuvid --enable-nvdec --enable-nvenc \
    --enable-amf --enable-libvpl \
    --disable-network \
    --disable-protocols --enable-protocol=file,pipe \
    --disable-demuxer=dash,imf,hls,rtsp,sdp,rtp,sap,cdxl,image_pcx_pipe,image_sunrast_pipe \
    --disable-decoder=cdxl,g2m,pcx,sunrast \
    || { tail -n 80 ffbuild/config.log; exit 1; }

make -j"${TEMNO_FFMPEG_JOBS:-8}"
make install

# Every source file and header this configuration compiled, read from the compiler's dependency files: the input to
# the notices for FFmpeg's permissively licensed files (scripts/ffmpeg/source-notices.ps1, WP-427). It reads the
# build tree only, so the binaries are unchanged. Kept, never shipped.
find . -name '*.d' -print0 | xargs -0 cat | tr -s ' \t\\' '\n\n\n' | sed -n '/:$/d; /^\//d; /^$/d; s|^\./||; p' \
    | sort -u > /work/compiled-sources.txt
grep -qx 'libavcodec/jrevdct.c' /work/compiled-sources.txt && grep -qx 'libavutil/x86/x86inc.asm' /work/compiled-sources.txt \
    || { echo "the dependency files did not list what was compiled" >&2; exit 1; }
# The headers it read from outside the tree: the image's libraries and hardware SDK headers (/opt/ffbuild), and the
# mingw-w64 and GCC headers (/opt/ct-ng). THIRD-PARTY-NOTICES.md carries their notices; this list shows which compiled.
find . -name '*.d' -print0 | xargs -0 cat | tr -s ' \t\\' '\n\n\n' | sed -n '/:$/d; /^\/opt\//p' \
    | sort -u > /work/compiled-headers-external.txt
grep -q '^/opt/ffbuild/include/' /work/compiled-headers-external.txt \
    || { echo "the dependency files listed no header from /opt/ffbuild/include" >&2; exit 1; }

rm -rf /out
mkdir -p /out
cp /work/prefix/bin/ffmpeg.exe /work/prefix/bin/ffprobe.exe /out/
cp /work/prefix/bin/*.dll /out/
cp COPYING.GPLv3 /out/LICENSE
cp /work/compiled-sources.txt /work/compiled-headers-external.txt /out/
x86_64-w64-mingw32-gcc --version | head -n 1 > /out/compiler.txt
ls -la /out
