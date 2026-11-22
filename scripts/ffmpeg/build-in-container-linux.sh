#!/bin/bash
# Builds the Linux FFmpeg that the Gateway runs (WP-425): the Linux twin of build-in-container.sh (WP-401).
# build-ffmpeg.ps1 -Rid linux-x64 runs this inside ghcr.io/btbn/ffmpeg-builds/linux64-gpl-shared, pinned by digest,
# which provides the x86_64-ffbuild-linux-gnu cross toolchain (built against an older glibc, so the binaries run on
# newer ones) and the static libraries named below. Inputs are staged in /work; the runtime files land in /out.
#
# Same source, patches and exclusions as the Windows build. Left out because they are Windows-only or need a GPU,
# which the Gateway's containers do not have: D3D11, D3D12, DXVA2, AMF, Intel VPL and NVIDIA's codec APIs.
# Added because ELF needs them: the binaries find their libraries through an $ORIGIN run path, and libgcc and
# libstdc++ (which x265 needs) are linked statically, so the files do not depend on the host's GCC runtime.
set -euo pipefail

release=9.1
# The 9.1 release commit's time (74e43935, 2026-11-19T18:47:40Z), as in the Windows build.
export SOURCE_DATE_EPOCH=1795114060

cd /work
rm -rf "ffmpeg-$release" prefix
tar -xJf "ffmpeg-$release.tar.xz"
cd "ffmpeg-$release"

# Upstream fixes ahead of the release, if any (none since 9.1, WP-442).
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
    --extra-ldflags="-pthread -static-libgcc -static-libstdc++" \
    --extra-version=temno1 \
    --enable-gpl --enable-version3 \
    --enable-shared --disable-static \
    --disable-debug --disable-doc --disable-ffplay \
    --disable-autodetect \
    --enable-pthreads \
    --enable-zlib --enable-lzma \
    --enable-libx264 --enable-libx265 --enable-libzimg --enable-libdav1d \
    --disable-network \
    --disable-protocols --enable-protocol=file,pipe \
    --disable-demuxer=dash,imf,hls,rtsp,sdp,rtp,sap,cdxl,image_pcx_pipe,image_sunrast_pipe \
    --disable-decoder=cdxl,g2m,pcx,sunrast \
    || { tail -n 80 ffbuild/config.log; exit 1; }

# The run paths go into config.mak after configure: passed as configure options, its eval expands "$$" to a process
# ID. Make turns "$$" into "$" once for the programs' link; the libraries' link rule sits in library.mak's $(eval),
# which expands it twice, so it needs "$$$$" (with "$$", "$O" expanded to nothing and left the path "RIGIN"). The
# single quotes keep the link's shell from expanding $ORIGIN. The checks below read both back.
cat >> ffbuild/config.mak <<'MAK'
LDEXEFLAGS += -Wl,-rpath,'$$ORIGIN/../lib'
LDSOFLAGS += -Wl,-rpath,'$$$$ORIGIN'
MAK

make -j"${TEMNO_FFMPEG_JOBS:-8}"
make install

# Every source file and header this configuration compiled, read from the compiler's dependency files: the input to
# the notices for FFmpeg's permissively licensed files (scripts/ffmpeg/source-notices.ps1). Kept, never shipped.
find . -name '*.d' -print0 | xargs -0 cat | tr -s ' \t\\' '\n\n\n' | sed -n '/:$/d; /^\//d; /^$/d; s|^\./||; p' \
    | sort -u > /work/compiled-sources.txt
grep -qx 'libavcodec/jrevdct.c' /work/compiled-sources.txt && grep -qx 'libavutil/x86/x86inc.asm' /work/compiled-sources.txt \
    || { echo "the dependency files did not list what was compiled" >&2; exit 1; }

rm -rf /out
mkdir -p /out/bin /out/lib
cp /work/prefix/bin/ffmpeg /work/prefix/bin/ffprobe /out/bin/
# Each library under its SONAME, the name the binaries load it by. NuGet carries no symbolic links.
for lib in /work/prefix/lib/lib*.so; do
    soname=$(readelf -d "$lib" | sed -n 's/.*Library soname: \[\(.*\)\].*/\1/p')
    [ -n "$soname" ] || { echo "no SONAME in $lib" >&2; exit 1; }
    cp -L "$lib" "/out/lib/$soname"
done
cp COPYING.GPLv3 /out/LICENSE
cp /work/compiled-sources.txt /out/compiled-sources.txt

# The checks ensure-ffmpeg.ps1 makes on the Windows build, run here because these binaries run only on Linux. Each
# list is read whole before it is searched: under pipefail, a reader that stops early (grep -q, head) fails the
# writer with SIGPIPE, which would read as "absent" and pass an exclusion check.
ff=/out/bin/ffmpeg
version_text=$("$ff" -hide_banner -version)
version_line=${version_text%%$'\n'*}
case "$version_line" in "ffmpeg version $release-temno1 "*) ;; *) echo "unexpected version: $version_line" >&2; exit 1;; esac
encoders=$("$ff" -hide_banner -encoders)
for name in libx264 libx265; do
    grep -q " $name " <<<"$encoders" || { echo "no $name encoder" >&2; exit 1; }
done
if grep -q ' libfdk' <<<"$encoders"; then echo "a nonfree libfdk encoder is listed" >&2; exit 1; fi
protocols=$("$ff" -hide_banner -protocols)
inputs=$(sed -n '/^Input:/,/^Output:/p' <<<"$protocols" | sed '1d;$d' | tr -d ' ' | paste -sd, -)
[ "$inputs" = "file,pipe" ] || { echo "input protocols are $inputs, not file,pipe" >&2; exit 1; }
# Each list starts after its legend's dashed line; its second column is the name (demuxers may list several).
demuxer_list=$("$ff" -hide_banner -demuxers)
decoder_list=$("$ff" -hide_banner -decoders)
demuxers=$(awk 'listed {print $2} /^ *-+ *$/ {listed = 1}' <<<"$demuxer_list" | tr ',' '\n')
decoders=$(awk 'listed {print $2} /^ *-+ *$/ {listed = 1}' <<<"$decoder_list")
grep -qx mov <<<"$demuxers" || { echo "the demuxer list did not parse" >&2; exit 1; }
grep -qx h264 <<<"$decoders" || { echo "the decoder list did not parse" >&2; exit 1; }
for name in dash imf hls rtsp sdp rtp sap cdxl pcx_pipe sunrast_pipe; do
    if grep -qx "$name" <<<"$demuxers"; then echo "the excluded $name demuxer is present" >&2; exit 1; fi
done
for name in cdxl g2m pcx sunrast; do
    if grep -qx "$name" <<<"$decoders"; then echo "the excluded $name decoder is present" >&2; exit 1; fi
done
/out/bin/ffprobe -hide_banner -version > /dev/null
# Each file may load only the C library's own parts and these libraries: no GCC runtime, no other library.
for file in /out/bin/* /out/lib/*; do
    for needed in $(readelf -d "$file" | sed -n 's/.*Shared library: \[\(.*\)\].*/\1/p'); do
        case "$needed" in
            libc.so.6|libm.so.6|libmvec.so.1|libpthread.so.0|libdl.so.2|librt.so.1|ld-linux-x86-64.so.2) ;;
            libav*.so.*|libsw*.so.*) [ -e "/out/lib/$needed" ] || { echo "$file needs a missing $needed" >&2; exit 1; } ;;
            *) echo "$file needs $needed" >&2; exit 1 ;;
        esac
    done
done
# Each file has exactly one run path, relative to itself: a relative or missing entry would search the working
# directory or the system for FFmpeg's libraries.
for file in /out/bin/* /out/lib/*; do
    case "$file" in /out/bin/*) want='$ORIGIN/../lib' ;; *) want='$ORIGIN' ;; esac
    paths=$(readelf -d "$file" | sed -n 's/.*(\(RPATH\|RUNPATH\)).*\[\(.*\)\].*/\2/p')
    [ "$paths" = "$want" ] || { echo "$file has run path '$paths', not '$want'" >&2; exit 1; }
done

configuration=$(grep '^configuration:' <<<"$version_text")
# CRLF, as the Windows build's notes: the file's hash is pinned.
printf '%s\r\n%s\r\n' "$version_line" "$configuration" > /out/configure.txt
"$CC" --version | head -n 1 > /out/compiler.txt
ls -laR /out
