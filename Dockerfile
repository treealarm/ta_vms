# =========================
# Builder
# =========================
FROM ta-deps AS builder

WORKDIR /build
COPY . /build

WORKDIR /build/media_server

RUN cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
RUN cmake --build build -j1

# сохранить libstdc++
#
# libva travels with the binary, and that is not optional. vcpkg builds ffmpeg against its own
# libva (2.24) while the runtime's distribution ships an older one, and libva is linked
# dynamically -- so the runtime's wins and the first hardware encode dies on
# `undefined symbol: vaMapBuffer2`. Lazily, too: the service starts fine and only fails once a
# HEVC stream is viewed. Shipping ours is safe in the direction that matters: libva looks up the
# driver entry point by trying __vaDriverInit_<major>_<minor> downwards from its own version, so
# a newer libva loads the distribution's older iHD; the reverse is what does not work.
RUN mkdir -p /runtime-libs && \
    cp $(g++ -print-file-name=libstdc++.so.6) /runtime-libs/ && \
    cp $(g++ -print-file-name=libgcc_s.so.1) /runtime-libs/ && \
    cp -P /vcpkg/installed/x64-linux/lib/libva.so.2* /runtime-libs/ && \
    cp -P /vcpkg/installed/x64-linux/lib/libva-drm.so.2* /runtime-libs/


# =========================
# Runtime
# =========================
# 24.04 rather than the 22.04 the builder uses: 22.04's intel-media-va-driver is too old to
# encode at all, so h264_vaapi would open on it and produce nothing. Going the other way is
# fine -- glibc only works forwards, and a binary built against 22.04's 2.35 runs on 2.39.
FROM ubuntu:24.04

# The VA-API driver, without which the encoder chain silently falls back to libx264. vainfo
# earns its size on a machine nobody can ssh into twice: "the device is not passed through",
# "the group is wrong" and "the driver did not load" look identical from the outside -- a
# service that merely burns CPU.
RUN apt-get update && apt-get install -y --no-install-recommends \
    intel-media-va-driver-non-free \
    libva2 \
    libva-drm2 \
    vainfo \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Копируем ВСЮ папку сборки
COPY --from=builder /build/media_server/build /app

# копируем runtime libc++ и libva
COPY --from=builder /runtime-libs /app

# Чтобы .so находились рядом
ENV LD_LIBRARY_PATH=/app
ENV LIBVA_DRIVER_NAME=iHD
# Where the driver actually is. The libva shipped above is vcpkg's, and its compiled-in search
# path is vcpkg's own build prefix -- a directory that does not exist in this image. Verified on
# the host: without this, va_openDriver() looks in /vcpkg/packages/... , returns -1, and the
# encoder chain falls back to libx264 having reported only "Failed to initialise VAAPI
# connection".
ENV LIBVA_DRIVERS_PATH=/usr/lib/x86_64-linux-gnu/dri

CMD ["./vms"]