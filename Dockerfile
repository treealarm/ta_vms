# =========================
# Builder
# =========================
FROM vms-deps AS builder

WORKDIR /build
COPY . /build

WORKDIR /build/media_server

RUN cmake -B build -G Ninja
RUN cmake --build build -j1

# сохранить libstdc++
RUN mkdir -p /runtime-libs && \
    cp $(g++ -print-file-name=libstdc++.so.6) /runtime-libs/ && \
    cp $(g++ -print-file-name=libgcc_s.so.1) /runtime-libs/


# =========================
# Runtime
# =========================
FROM ubuntu:22.04

WORKDIR /app

# Копируем ВСЮ папку сборки
COPY --from=builder /build/media_server/build /app

# копируем runtime libc++
COPY --from=builder /runtime-libs /app

# Чтобы .so находились рядом
ENV LD_LIBRARY_PATH=/app

CMD ["./vms"]