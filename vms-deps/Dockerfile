# syntax=docker/dockerfile:1.5
FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive
ENV VCPKG_DEFAULT_BINARY_CACHE=/vcpkg/binary-cache
ENV CMAKE_TOOLCHAIN_FILE=/vcpkg/scripts/buildsystems/vcpkg.cmake

ENV VCPKG_BUILD_TYPE=release

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

RUN --mount=type=cache,target=/vcpkg/buildtrees \
    --mount=type=cache,target=/vcpkg/packages \
    --mount=type=cache,target=/vcpkg/downloads \
    --mount=type=cache,target=/vcpkg/binary-cache \
	VCPKG_BUILD_TYPE=release \
    /vcpkg/vcpkg install \
        protobuf \
        grpc \
		spdlog \
        ffmpeg[gpl,x264,x265,opus,vpx,aom,openssl]:x64-linux