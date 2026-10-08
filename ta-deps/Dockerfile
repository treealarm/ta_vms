# syntax=docker/dockerfile:1.5
FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive
ENV VCPKG_DEFAULT_BINARY_CACHE=/vcpkg/binary-cache
ENV CMAKE_TOOLCHAIN_FILE=/vcpkg/scripts/buildsystems/vcpkg.cmake

# Установка компиляторов и инструментов
RUN apt-get update && apt-get install -y \
    software-properties-common curl zip unzip tar git \
    build-essential cmake ninja-build pkg-config nasm \
    libdrm-dev \
    && add-apt-repository ppa:ubuntu-toolchain-r/test -y \
    && apt-get update && apt-get install -y gcc-13 g++-13 \
    && update-alternatives --install /usr/bin/gcc gcc /usr/bin/gcc-13 100 \
    && update-alternatives --install /usr/bin/g++ g++ /usr/bin/g++-13 100 \
    && rm -rf /var/lib/apt/lists/*

# Установка vcpkg и зависимостей
RUN git clone https://github.com/microsoft/vcpkg.git /vcpkg
RUN /vcpkg/bootstrap-vcpkg.sh

# Release-only triplet overlay (see triplets/x64-linux.cmake) — same triplet name, so every
# `:x64-linux` package suffix and downstream find_package() keeps resolving unchanged; only the
# install-time behavior (skip building the debug config) changes.
COPY triplets/ /vcpkg-overlay-triplets/

# Shared C++ deps base for both this repo's media_server (`vms`) and video_a's analytics-worker
# (hence openvino, which media_server itself doesn't link but video_a does).
#
# `vaapi` is here because media_server transcodes live HEVC->H.264 for WebRTC on the CPU, one
# libx264 encode per viewed stream, and that is the largest continuous cost in the service. It
# needs libdrm-dev above: libva has no vcpkg port for it, and its portfile fails at meson
# configure with an error that does not name the cause.
#
# This list carried qsv/vaapi once before, for roi_transcode, and they were removed because two
# consumers with no GPU were paying for a hardware dependency. That reasoning still holds for
# everything past VA-API: `qsv` pulls the oneVPL dispatcher, which is useless without
# libmfx-gen1.2 in a runtime image, and nlohmann-json is linked by roi_transcode alone. Those
# stay in roi-deps/Dockerfile. video_a pays for `vaapi` in image size and nothing else.
#
# Building it is not the same as using it. The encoders are in libavcodec, but h264_vaapi also
# needs a current iHD driver in the *runtime* image and /dev/dri passed into the container —
# see the runtime stage of the root Dockerfile. Without either, the encoder chain falls back to
# libx264 and everything still looks fine.
#
# openvino lists its features instead of taking the port's defaults, which build every model
# frontend -- paddle, pytorch, tensorflow and tensorflow-lite included. analytics-worker reads
# .onnx and .xml (IR) models only, so those four were most of openvino's build time and memory
# for nothing: tensorflow alone is the frontend a single-threaded rebuild spends hours in. The
# `core` entry is what switches the defaults off. gpu and npu stay, for ANALYTICS_DEVICE.
#
# VCPKG_MAX_CONCURRENCY=2: more than that and some port runs the Docker VM out of memory.
RUN --mount=type=cache,target=/vcpkg/buildtrees \
    --mount=type=cache,target=/vcpkg/packages \
    --mount=type=cache,target=/vcpkg/downloads \
    --mount=type=cache,target=/vcpkg/binary-cache \
    VCPKG_MAX_CONCURRENCY=2 \
    /vcpkg/vcpkg install \
        --overlay-triplets=/vcpkg-overlay-triplets \
        protobuf \
        grpc \
        spdlog \
        ffmpeg[gpl,x264,x265,opus,vpx,aom,openssl,vaapi]:x64-linux \
        openvino[core,cpu,gpu,npu,auto,hetero,auto-batch,ir,onnx]:x64-linux