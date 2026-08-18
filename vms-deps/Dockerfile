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
# qsv/vaapi are for roi_transcode: ROI encoding on Intel goes through hevc_qsv ->
# mfxExtEncoderROI, with hevc_vaapi as the fallback on the same iGPU. They pull vcpkg's libvpl
# and libva, and libva in turn needs system libdrm headers — without libdrm-dev above it fails
# at meson configure, which is not obvious from the error vcpkg prints.
#
# Building these is not enough to use them: the encoders live in libavcodec, but HEVC ROI on
# Alder Lake needs a current iHD driver in the *runtime* image plus /dev/dri. Without that
# `--encoder auto` silently falls back to x265 and everything still looks fine.
RUN --mount=type=cache,target=/vcpkg/buildtrees \
    --mount=type=cache,target=/vcpkg/packages \
    --mount=type=cache,target=/vcpkg/downloads \
    --mount=type=cache,target=/vcpkg/binary-cache \
    VCPKG_MAX_CONCURRENCY=1 \
    /vcpkg/vcpkg install \
        --overlay-triplets=/vcpkg-overlay-triplets \
        protobuf \
        grpc \
		spdlog \
        nlohmann-json \
        ffmpeg[gpl,x264,x265,opus,vpx,aom,openssl,qsv,vaapi]:x64-linux \
        openvino:x64-linux