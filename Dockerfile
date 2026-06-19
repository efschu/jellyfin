# ============================================================
# Jellyfin with FFmpeg VapourSynth Filter Support
# ============================================================
# This Dockerfile builds Jellyfin with FFmpeg that has the
# VapourSynth filter (-vf vapoursynth=...) built-in.
#
# Usage:
#   docker build -t jellyfin-vsfilter .
# ============================================================

# ============================================================
# Stage 1: Build FFmpeg with VapourSynth support
# ============================================================
FROM ubuntu:22.04 AS ffmpeg-builder

# Limit build threads to 4 for stability
ENV MAKEFLAGS="-j4"
ENV DEBIAN_FRONTEND=noninteractive

# Install build dependencies
RUN apt-get update && apt-get install -y \
    build-essential cmake pkg-config nasm yasm libtool \
    libc6-dev wget git \
    libssl-dev \
    python3 python3-pip python3-dev \
    python3-numpy cython3 \
    && rm -rf /var/lib/apt/lists/*

# Build VapourSynth from source
WORKDIR /build
RUN git clone --depth 1 --branch R73 https://github.com/vapoursynth/vapoursynth.git
WORKDIR /build/vapoursynth
RUN cmake -B build -DCMAKE_INSTALL_PREFIX=/usr/local \
    -DCMAKE_DISABLE_PREBUILD=ON \
    -DREGEX_DISABLED=ON \
    && cmake --build build -j4 \
    && cmake --install build \
    && ldconfig

# Install video codec dependencies
RUN apt-get update && apt-get install -y \
    libx264-dev libx265-dev libnuma-dev libvpx-dev \
    libmp3lame-dev libopus-dev \
    libass-dev libfreetype6-dev libfribidi-dev libharfbuzz-dev \
    libsdl2-dev libvpl-dev libdav1d-dev libaom-dev \
    && rm -rf /var/lib/apt/lists/*

# Clone and build FFmpeg with VapourSynth support
WORKDIR /build
RUN git clone --depth 1 --branch master https://github.com/efschu/FFmpeg.git ffmpeg
WORKDIR /build/ffmpeg
RUN ./configure \
    --prefix=/usr/local \
    --bindir=/usr/local/bin \
    --libdir=/usr/local/lib \
    --incdir=/usr/local/include \
    --datadir=/usr/local/share \
    --mandir=/usr/local/share/man \
    --disable-doc --disable-static --enable-shared \
    --enable-gpl --enable-version3 \
    --enable-libx264 --enable-libx265 --enable-libvpx \
    --enable-libmp3lame --enable-libopus --enable-libass \
    --enable-libfreetype --enable-libfribidi --enable-libharfbuzz \
    --enable-libvpl --enable-libdav1d --enable-libaom \
    --enable-vapoursynth --enable-postproc \
    --extra-cflags="-I/usr/local/include" \
    --extra-ldflags="-L/usr/local/lib" \
    && make -j4 \
    && make install \
    && ldconfig

# Verify VapourSynth filter is available
RUN /usr/local/bin/ffmpeg -filters 2>/dev/null | grep -i vapour && echo "SUCCESS: VapourSynth filter available"

# ============================================================
# Stage 2: Jellyfin Runtime
# ============================================================
FROM jellyfin/jellyfin:10.9

USER root

# Install runtime dependencies
RUN apt-get update && apt-get install -y \
    python3 python3-pip python3-dev \
    libdrm2 libva2 libva-drm2 \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get clean

# Copy FFmpeg with VapourSynth support
COPY --from=ffmpeg-builder /usr/local/bin/ffmpeg /usr/local/bin/ffmpeg
COPY --from=ffmpeg-builder /usr/local/bin/ffprobe /usr/local/bin/ffprobe
COPY --from=ffmpeg-builder /usr/local/lib/libavcodec.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/lib/libavformat.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/lib/libavutil.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/lib/libswscale.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/lib/libswresample.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/lib/libavfilter.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/lib/libpostproc.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/lib/libvapoursynth.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/lib/libvsscript.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/include/* /usr/local/include/
RUN ldconfig

ENV FFMPEG_PATH=/usr/local/bin/ffmpeg
ENV FFPROBE_PATH=/usr/local/bin/ffprobe

# Create directories for VapourSynth scripts and models
USER jellyfin
RUN mkdir -p /config/vapoursynth \
    && mkdir -p /config/vapoursynth/models \
    && mkdir -p /cache/transcodes

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD /usr/local/bin/ffmpeg -version > /dev/null 2>&1 || exit 1

EXPOSE 8096 8920
VOLUME ["/config", "/cache", "/media"]

CMD ["jellyfin", "--ffmpeg", "/usr/local/bin/ffmpeg", "--ffprobe", "/usr/local/bin/ffprobe"]
