# syntax=docker/dockerfile:1.5
FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive
ENV VCPKG_DEFAULT_BINARY_CACHE=/vcpkg/binary-cache
ENV CMAKE_TOOLCHAIN_FILE=/vcpkg/scripts/buildsystems/vcpkg.cmake

# Установка компиляторов и инструментов
RUN apt-get update && apt-get install -y \
    software-properties-common curl zip unzip tar git \
    build-essential cmake ninja-build pkg-config nasm \
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
# Nothing GPU-related belongs here. qsv/vaapi and the system libdrm they need used to sit in this
# list for roi_transcode, which made two consumers with no GPU carry a hardware dependency and a
# driver-ABI question that is none of their business. That moved to roi-deps/Dockerfile, which is
# also where nlohmann-json went — roi_transcode is the only thing that ever linked it.
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
        ffmpeg[gpl,x264,x265,opus,vpx,aom,openssl]:x64-linux \
        openvino:x64-linux