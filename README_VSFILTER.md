# Jellyfin with FFmpeg VapourSynth Filter Pipeline

This branch adds **VapourSynth filter pipeline** support to Jellyfin, enabling AI-powered upscaling and frame interpolation using FFmpeg's built-in `-vf vapoursynth=...` filter.

## Architecture

### Standard Transcoding
```
FFmpeg → Standard Encoding → HLS
```

### VS Filter Pipeline
```
FFmpeg with -vf vapoursynth=... → In-Process VapourSynth → AI Processing → HLS
```

**Benefits:**
- Single FFmpeg process (no external vspipe)
- In-process VapourSynth execution (lower latency)
- AI upscaling with Real-ESRGAN
- Frame interpolation with RIFE
- Direct TensorRT support for GPU acceleration

## Requirements

1. **FFmpeg with VapourSynth support**
   - Use the fork: https://github.com/efschu/FFmpeg
   - Or build FFmpeg with `--enable-vapoursynth`

2. **VapourSynth Python packages**
   ```bash
   pip install vapoursynth numpy
   pip install vs-realesrgan vsrife  # Optional: AI plugins
   ```

3. **TensorRT** (optional, for GPU acceleration)
   - See: https://github.com/AmusementClub/vstrt

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `EnableVsFilterPipeline` | false | Enable/disable the pipeline |
| `VsFilterPreset` | anime-upscaled | Preset: anime-upscaled, anime-interpolated, custom |
| `VsFilterCustomScript` | - | Custom VapourSynth script |
| `VsUpscaleModel` | realesr-animevideov3 | Upscaling model |
| `VsInterpolationModel` | rife-4.25 | Interpolation model |
| `VsTargetWidth` | 3840 | Output width |
| `VsTargetHeight` | 2160 | Output height |
| `VsTargetFpsNum` | 60000 | FPS numerator (~60fps) |
| `VsTargetFpsDen` | 1001 | FPS denominator |
| `VsPixelFormat` | YUV420P10 | Output pixel format |
| `VsThreads` | 0 | Threads (0 = auto) |
| `VsEncoderArgs` | - | Custom FFmpeg encoder args |

## Presets

| Preset | Upscaling | Interpolation | Target FPS |
|--------|----------|--------------|------------|
| anime-upscaled | 2x Real-ESRGAN | None | Original |
| anime-interpolated | 2x Real-ESRGAN | RIFE | ~60fps |
| custom | User script | User script | User script |

## Building

### Docker (Recommended)

```bash
# Build the Docker image
docker build -t jellyfin-vsfilter .

# Run
docker run -d \
  --name jellyfin \
  -p 8096:8096 \
  -p 8920:8920 \
  -v /path/to/config:/config \
  -v /path/to/media:/media \
  jellyfin-vsfilter
```

### Manual Build

1. Build FFmpeg with VapourSynth:
   ```bash
   git clone https://github.com/efschu/FFmpeg.git
   cd FFmpeg
   ./configure --enable-vapoursynth ...
   make -j4 && make install
   ```

2. Build Jellyfin:
   ```bash
   dotnet build
   ```

## Custom VapourSynth Script

Example for custom processing:

```python
import vapoursynth as vs
core = vs.core

# Load TensorRT plugin
core.std.LoadPlugin('/usr/lib/vapoursynth/libvstrt.so')

# Read input
clip = clip.resize.Bicubic(format=vs.RGBS, matrix_in_s='709')

# Custom upscaling with TensorRT
clip = core.trt.Model(clip, engine='/models/upscaler.engine')

# Frame interpolation
clip = core.rife.RIFE(clip, model='rife.25', factor_num=60000, factor_den=1001, trt=True)

# Convert output
clip = clip.resize.Bicubic(format=vs.YUV420P10, matrix_s='709')

clip.set_output()
```

## Troubleshooting

### "No filter named 'vapoursynth'"
FFmpeg doesn't have VapourSynth support. Rebuild with `--enable-vapoursynth`.

### "No module named 'vsrealesrgan'"
Install the plugin:
```bash
pip install vs-realesrgan
```

### Out of memory
Reduce output resolution or disable frame interpolation.

## Credits

- [VapourSynth](https://vapoursynth.com)
- [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN)
- [RIFE](https://github.com/megvii-research/ECCV2022-RIFE)
- [FFmpeg VS Filter](https://github.com/efschu/FFmpeg)
