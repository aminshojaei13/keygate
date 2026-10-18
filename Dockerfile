# ── Build frontend ──
# Pinned to the build host's platform: the output is static, arch-neutral
# assets, so building it once natively (never under emulation) is both
# correct and much faster for the arm64 image.
FROM --platform=$BUILDPLATFORM oven/bun:1 AS frontend
WORKDIR /app/web
COPY web/package.json web/bun.lock* ./
RUN bun install --frozen-lockfile
COPY web/ .
RUN bun run build

# ── Build backend ──
# Runs on the build host's platform and cross-compiles to the target arch
# below (CGO disabled, so it's a pure Go cross-build). This keeps the
# expensive compile off QEMU. The small runtime stage still runs its apk
# install under the target arch, so the arm64 image is faster to build but
# not fully emulation-free.
FROM --platform=$BUILDPLATFORM golang:1.27-alpine AS backend
RUN apk add --no-cache git
WORKDIR /app
COPY go.mod go.sum ./
RUN go mod download
COPY . .
COPY --from=frontend /app/web/dist ./web/dist
ARG VERSION=dev
ARG COMMIT=unknown
ARG BUILD_DATE=unknown
# TARGETOS/TARGETARCH are provided by buildx per requested --platform.
ARG TARGETOS
ARG TARGETARCH
RUN CGO_ENABLED=0 GOOS=${TARGETOS} GOARCH=${TARGETARCH} go build -trimpath \
    -ldflags "-s -w \
      -X github.com/tabloy/keygate/internal/version.Version=${VERSION} \
      -X github.com/tabloy/keygate/internal/version.Commit=${COMMIT} \
      -X github.com/tabloy/keygate/internal/version.BuildDate=${BUILD_DATE}" \
    -o /keygate ./cmd/server

# ── Runtime ──
FROM alpine:3.21
RUN apk add --no-cache ca-certificates tzdata curl && \
    addgroup -S -g 10001 keygate && \
    adduser -S -u 10001 -G keygate keygate
WORKDIR /app
COPY --from=backend --chown=keygate:keygate /keygate /usr/local/bin/keygate
COPY --from=backend --chown=keygate:keygate /app/db/migrations /app/db/migrations
COPY --from=backend --chown=keygate:keygate /app/web/dist /app/web/dist
COPY --from=backend --chown=keygate:keygate /app/docs /app/docs

LABEL org.opencontainers.image.title="Keygate" \
      org.opencontainers.image.description="Open source license management platform" \
      org.opencontainers.image.vendor="Tabloy" \
      org.opencontainers.image.url="https://keygate.app" \
      org.opencontainers.image.source="https://github.com/tabloy/keygate" \
      org.opencontainers.image.licenses="AGPL-3.0"

USER keygate
EXPOSE 9000
ENV PORT=9000
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl -sf http://localhost:${PORT:-9000}/health || exit 1
ENTRYPOINT ["keygate"]
