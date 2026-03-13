FROM vms-deps AS builder

WORKDIR /build

COPY . /build

WORKDIR /build/media_server

RUN cmake -B build -G Ninja
RUN cmake --build build -j

# =========================
# Runtime
# =========================
FROM vms-deps AS runtime

WORKDIR /app

# Копируем только бинарник
COPY --from=builder /build/media_server/build/vms .

CMD ["./vms"]