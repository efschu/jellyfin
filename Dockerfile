# ============================================================
# Jellyfin with FFmpeg VapourSynth Filter Support
# ============================================================
FROM ubuntu:22.04 AS ffmpeg-builder

# Limit build threads to 4 for stability
ENV MAKEFLAGS="-j4"
ENV DEBIAN_FRONTEND=noninteractive

# Install build dependencies
RUN apt-get update && apt-get install -y \
    build-essential cmake pkg-config nasm yasm libtool autoconf automake \
    libc6-dev wget git \
    libssl-dev \
    python3 python3-pip python3-dev \
    python3-numpy \
    && rm -rf /var/lib/apt/lists/*

# Install specific Cython version compatible with VapourSynth R73
RUN pip3 install --upgrade pip && pip3 install cython

# Create build directory
RUN mkdir -p /build

# Build zimg from source (ubuntu has 3.0.3, need >= 3.0.5)
WORKDIR /build
RUN git clone --depth 1 --branch v3.0 https://github.com/sekrit-twc/zimg.git
WORKDIR /build/zimg
RUN ./autogen.sh && \
    ./configure --disable-static PREFIX=/usr/local && \
    make -j4 && make install && ldconfig

# Build VapourSynth from source using autotools
WORKDIR /build
RUN git clone --depth 1 --branch R73 https://github.com/vapoursynth/vapoursynth.git
WORKDIR /build/vapoursynth
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

# Build dav1d >= 1.0.0 from source (ubuntu has 0.9.x)
WORKDIR /build
RUN git clone --depth 1 --branch 1.4.3 https://github.com/videolan/dav1d.git
WORKDIR /build/dav1d
RUN meson setup build --prefix=/usr/local --buildtype=release && \
    ninja -C build && ninja -C build install && ldconfig

# Build libvpl >= 2.6 from source (ubuntu has 2.5.x, latest is v2023.4.0)
WORKDIR /build
RUN git clone --depth 1 --branch v2023.4.0 https://github.com/intel/libvpl.git
WORKDIR /build/libvpl
RUN cmake -B build -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX=/usr/local \
    -DBUILD_EXAMPLES=OFF -DBUILD_TESTS=OFF && \
    cmake --build build && cmake --install build && ldconfig

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
    --enable-vapoursynth \
    --extra-cflags="-I/usr/local/include" \
    --extra-ldflags="-L/usr/local/lib" \
    && make -j4 \
    && make install \
    && ldconfig
    # Verify FFmpeg is installed
    RUN /usr/local/bin/ffmpeg -version | head -1 && echo "FFmpeg installed successfully"
# ============================================================
# Stage 2: Jellyfin Runtime
# ============================================================
FROM jellyfin/jellyfin:10.9

USER root

RUN apt-get update && apt-get install -y \
    python3 python3-pip python3-dev \
    libdrm2 libva2 libva-drm2 libasound2 \
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
COPY --from=ffmpeg-builder /usr/local/lib/libavdevice.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/lib/libvapoursynth.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/lib/libvsscript.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/lib/libzimg.so* /usr/local/lib/
COPY --from=ffmpeg-builder /usr/local/include/* /usr/local/include/
# First run ldconfig to set up the cache, then create explicit .so symlinks
RUN ldconfig \
    && for lib in /usr/local/lib/lib*.so.*; do \
         if [ -f "$lib" ] && [ ! -L "$lib" ]; then \
           ln -sf "$(basename $lib)" "${lib%.*}" 2>/dev/null || true; \
         fi \
       done

ENV FFMPEG_PATH=/usr/local/bin/ffmpeg
ENV FFPROBE_PATH=/usr/local/bin/ffprobe
ENV LD_LIBRARY_PATH=/usr/local/lib

# Create directories for VapourSynth scripts and models

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
