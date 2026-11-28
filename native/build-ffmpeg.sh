#!/usr/bin/env bash
# Configures, builds and installs a minimal LGPL FFmpeg with MSVC: avcodec (aac and sbc decoders, sbc encoder only;
# sbc also does mSBC) + avutil + swresample.
# --enable-small strips help/name strings; --optflags=-O2 keeps speed codegen (measured: same decode speed as plain
# -O2, ~75 KB smaller; MSVC -O1 would be ~12% slower). -MT + -NODEFAULTLIB:libucrt.lib + ucrt.lib = hybrid CRT.
# -guard:cf (compile + link) = Control Flow Guard.
# arm64: cross-compiled (configure runs in x64 MSYS2, also on Arm64 Windows) and without FFmpeg's hand-written
# assembly, which MSVC can only assemble through gas-preprocessor + armasm64; the C code decodes AAC and SBC as well.
# Called by build-ffmpeg.ps1 from an MSVC environment for the target.
# Args: <source dir> <build dir> <install prefix> <arch: x64 or arm64>
set -euo pipefail
src=$1
build=$2
prefix=$3
arch=$4

case "$arch" in
    x64) target=(--arch=x86_64) ;;
    arm64) target=(--arch=aarch64 --enable-cross-compile --disable-asm) ;;
    *) echo "unknown arch: $arch" >&2; exit 1 ;;
esac

cd "$build"
"$src/configure" \
    --prefix="$prefix" \
    --toolchain=msvc "${target[@]}" --target-os=win64 \
    --enable-shared --disable-static \
    --disable-autodetect --enable-w32threads \
    --disable-programs --disable-doc --disable-debug \
    --disable-avformat --disable-avfilter --disable-avdevice --disable-swscale --disable-postproc \
    --disable-network --disable-pixelutils --disable-iamf \
    --disable-everything --enable-decoder=aac --enable-decoder=sbc --enable-encoder=sbc \
    --enable-small --optflags=-O2 \
    --extra-cflags="-MT -guard:cf" --extra-ldflags="-NODEFAULTLIB:libucrt.lib -guard:cf" --extra-libs=ucrt.lib
make -j"$(nproc)"
make install
