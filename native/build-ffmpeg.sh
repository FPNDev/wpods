#!/usr/bin/env bash
# Configures, builds and installs a minimal LGPL FFmpeg with MSVC: avcodec (aac decoder only) + avutil + swresample.
# --enable-small strips help/name strings; --optflags=-O2 keeps speed codegen (measured: same decode speed as plain
# -O2, ~75 KB smaller; MSVC -O1 would be ~12% slower). -MT + -NODEFAULTLIB:libucrt.lib + ucrt.lib = hybrid CRT.
# -guard:cf (compile + link) = Control Flow Guard.
# Called by build-ffmpeg.ps1 from an MSVC x64 environment. Args: <source dir> <build dir> <install prefix>
set -euo pipefail
src=$1
build=$2
prefix=$3

cd "$build"
"$src/configure" \
    --prefix="$prefix" \
    --toolchain=msvc --arch=x86_64 --target-os=win64 \
    --enable-shared --disable-static \
    --disable-autodetect --enable-w32threads \
    --disable-programs --disable-doc --disable-debug \
    --disable-avformat --disable-avfilter --disable-avdevice --disable-swscale --disable-postproc \
    --disable-network --disable-pixelutils --disable-iamf \
    --disable-everything --enable-decoder=aac \
    --enable-small --optflags=-O2 \
    --extra-cflags="-MT -guard:cf" --extra-ldflags="-NODEFAULTLIB:libucrt.lib -guard:cf" --extra-libs=ucrt.lib
make -j"$(nproc)"
make install
