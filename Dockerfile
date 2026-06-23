# ============================================================
# Jellyfin with FFmpeg VapourSynth Demuxer Support
# ============================================================
# Builds FFmpeg with VapourSynth demuxer support, then assembles
# a Jellyfin runtime with everything needed for the VS Filter
# pipeline (4kx2 quality with AI upscaling + frame interpolation).
#
# Uses VapourSynth demuxer (-f vapoursynth -i script.vpy) approach
# since the VS filter (-vf vapoursynth=) requires extensive FFmpeg
# fork changes.
# ============================================================

# ============================================================
# Stage 1: Build FFmpeg + VapourSynth
# ============================================================
FROM ubuntu:22.04 AS ffmpeg-builder

# Limit build threads to 4 for stability
ENV MAKEFLAGS="-j4"
ENV DEBIAN_FRONTEND=noninteractive
ENV PREFIX=/usr/local

# Install build dependencies including Python 3.10 and VapourSynth deps
RUN apt-get update && apt-get install -y \
    build-essential cmake pkg-config nasm yasm libtool autoconf automake \
    libc6-dev wget git \
    libssl-dev \
    python3.10 python3.10-dev python3.10-venv \
    python3 python3-pip python3-dev \
    && rm -rf /var/lib/apt/lists/*

# Set up Python 3.10 venv (this is what VapourSynth will be built against)
RUN python3.10 -m venv /opt/venv
ENV PATH="/opt/venv/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
RUN /opt/venv/bin/pip install --no-cache-dir --upgrade pip && \
    /opt/venv/bin/pip install --no-cache-dir numpy "cython<3.0"

# Create build directory
RUN mkdir -p /build

# Build zimg >= 3.0.5 from source
WORKDIR /build
RUN git clone --depth 1 --branch v3.0 https://github.com/sekrit-twc/zimg.git
WORKDIR /build/zimg
RUN ./autogen.sh && \
    ./configure --disable-static PREFIX=/usr/local && \
    make -j4 && make install && ldconfig

# Build VapourSynth R73 from source (Python 3.10 binding)
WORKDIR /build
RUN git clone --depth 1 --branch R73 https://github.com/vapoursynth/vapoursynth.git
WORKDIR /build/vapoursynth
# Patch the Cython source to use Python 3.7-compatible syntax
# Cython 0.29.x doesn't understand Python 3.8+ positional-only parameter syntax (/)
# Use | as sed delimiter to avoid issues with /
RUN sed -i 's|key, /, default=None|key, default=None|g' src/cython/vapoursynth.pyx && \
    sed -i 's|key, /, default=_EMPTY|key, default=_EMPTY|g' src/cython/vapoursynth.pyx && \
    sed -i 's|key, default=0, /)|key, default=0)|g' src/cython/vapoursynth.pyx
RUN ./autogen.sh && \
    ./configure PREFIX=/usr/local && \
    make -j4 && make install && ldconfig

# Install video codec dependencies
RUN apt-get update && apt-get install -y \
    libx264-dev libx265-dev libnuma-dev libvpx-dev \
    libmp3lame-dev libopus-dev \
    libass-dev libfreetype6-dev libfribidi-dev libharfbuzz-dev \
    libsdl2-dev libaom-dev nasm meson git cmake \
    && rm -rf /var/lib/apt/lists/*

# Build dav1d >= 1.0.0 from source
WORKDIR /build
RUN git clone --depth 1 --branch 1.4.3 https://github.com/videolan/dav1d.git
WORKDIR /build/dav1d
RUN meson setup build --prefix=/usr/local --libdir=lib --buildtype=release && \
    ninja -C build && ninja -C build install && ldconfig

# Build libvpl >= 2.6 from source
WORKDIR /build
RUN git clone --depth 1 --branch v2023.4.0 https://github.com/intel/libvpl.git
WORKDIR /build/libvpl
RUN cmake -B build -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX=/usr/local \
    -DBUILD_EXAMPLES=OFF -DBUILD_TESTS=OFF && \
    cmake --build build -j4 && cmake --install build && ldconfig

# Clone and build FFmpeg with VapourSynth demuxer
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
    --enable-vapoursynth \
    --extra-cflags="-I/usr/local/include" \
    --extra-ldflags="-L/usr/local/lib" \
    && make -j4 \
    && make install \
    && ldconfig

# Verify VapourSynth demuxer is available
RUN /usr/local/bin/ffmpeg -version | head -1 && \
    /usr/local/bin/ffmpeg -demuxers 2>/dev/null | grep -i vapoursynth && \
    echo "SUCCESS: FFmpeg with VapourSynth demuxer built"

# ============================================================
# Stage 2: Jellyfin Runtime
# ============================================================
FROM jellyfin/jellyfin:10.9

USER root

# Install runtime dependencies
RUN apt-get update && apt-get install -y \
    python3 python3-pip python3-dev \
    libdrm2 libva2 libva-drm2 libasound2 libxv1 libvpl2 \
    libxcb1 libxcb-shm0 libxcb-xfixes0 \
    libx11-6 libxext6 \
    libgl1-mesa-glx \
    libsdl2-2.0-0 \
    libgomp1 \
    libass9 libfreetype6 libfribidi0 \
    libfontconfig1 \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get clean

# Copy FFmpeg with VapourSynth support
COPY --from=ffmpeg-builder /usr/local/bin/ffmpeg /usr/local/bin/ffmpeg
COPY --from=ffmpeg-builder /usr/local/bin/ffprobe /usr/local/bin/ffprobe
COPY --from=ffmpeg-builder /usr/local/lib/ /usr/local/lib/

# Copy system libraries that FFmpeg depends on
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libx264.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libx265.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libmp3lame.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libopus.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libvpx.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libass.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libfreetype.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libfribidi.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libharfbuzz.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libfontconfig.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libpng16.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libxml2.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libtheora.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libvorbis.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libogg.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libsndio.so.7* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libdav1d.so* /usr/lib/x86_64-linux-gnu/
COPY --from=ffmpeg-builder /usr/lib/x86_64-linux-gnu/libvpl.so* /usr/lib/x86_64-linux-gnu/

# Copy Python 3.10 with VapourSynth module
# VapourSynth was built against Python 3.10, so we need the Python 3.10 runtime
COPY --from=ffmpeg-builder /usr/bin/python3.10 /usr/bin/python3.10
COPY --from=ffmpeg-builder /opt/venv/lib/python3.10/site-packages/ /opt/venv/lib/python3.10/site-packages/

# Copy VapourSynth C headers
COPY --from=ffmpeg-builder /usr/local/include/vapoursynth/ /usr/local/include/vapoursynth/

# Create proper symlinks for shared libraries
RUN ldconfig && \
    for lib in /usr/local/lib/lib*.so.*.*.*; do \
        target="${lib%.*}"; \
        [ -e "$target" ] || ln -sf "$(basename $lib)" "$target"; \
        target="${target%.*}"; \
        [ -e "$target" ] || ln -sf "$(basename $lib)" "$target"; \
        target="${target%.*}"; \
        [ -e "$target" ] || ln -sf "$(basename $lib)" "$target"; \
    done && \
    for lib in /usr/local/lib/lib*.so.*; do \
        [ -L "${lib%.*}" ] || ln -sf "$(basename $lib)" "${lib%.*}"; \
    done && \
    ldconfig

# Environment variables
ENV LD_LIBRARY_PATH="/usr/local/lib:/usr/lib/x86_64-linux-gnu"
ENV PYTHONHOME="/opt/venv"
ENV PYTHONPATH="/opt/venv/lib/python3.10/site-packages:/opt/venv/lib/python3.10"
ENV FFMPEG_PATH=/usr/local/bin/ffmpeg
ENV FFPROBE_PATH=/usr/local/bin/ffprobe

# Create directories for VapourSynth scripts
RUN mkdir -p /config/vapoursynth \
    && mkdir -p /config/vapoursynth/models \
    && mkdir -p /cache/transcodes \
    && chown -R 1000:1000 /config /cache

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD /usr/local/bin/ffmpeg -version > /dev/null 2>&1 || exit 1

EXPOSE 8096 8920
VOLUME ["/config", "/cache", "/media"]

CMD ["jellyfin", "--ffmpeg", "/usr/local/bin/ffmpeg", "--ffprobe", "/usr/local/bin/ffprobe"]
