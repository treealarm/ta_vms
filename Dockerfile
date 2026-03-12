FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y \
    build-essential \
    cmake \
    ninja-build \
    pkg-config \
    protobuf-compiler \
    libprotobuf-dev \
    libgrpc++-dev \
    libavformat-dev \
    libavcodec-dev \
    libavutil-dev \
    libswscale-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY . .

WORKDIR /app/media_server

RUN cmake -B build -G Ninja
RUN cmake --build build -j

CMD ["./build/vms"]