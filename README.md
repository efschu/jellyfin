# 4K×2 VapourSynth Filter Pipeline for Jellyfin

## Overview

This fork adds a new **4K×2 quality option** with AI-powered upscaling and frame interpolation using FFmpeg's built-in VapourSynth filter.

## Architecture

### Old Approach (vspipe pipeline)
```
FFmpeg-1 → Named Pipe 1 → [vspipe] → Named Pipe 2 → FFmpeg-2 → HLS
```
- 3 separate processes
- Named pipes for IPC
- Higher latency and overhead

### New Approach (VS Filter pipeline)
```
FFmpeg with -vf vapoursynth=... → In-Process VapourSynth → HLS
```
- Single FFmpeg process
- In-process VapourSynth execution
- Lower latency and overhead
- Direct TensorRT support

## Files Changed

### Backend (C#)

| File | Change |
|------|--------|
| `MediaBrowser.Model/Configuration/EncodingOptions.cs` | Added VS Filter configuration options |
| `MediaBrowser.MediaEncoding/Transcoding/VsFilterPipeline.cs` | **NEW** Single-FFmpeg VS pipeline |
| `MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs` | Added VS Filter mode detection |
| `Jellyfin.Api/Controllers/VsFilterController.cs` | **NEW** API controller for config |

### Frontend (TypeScript/React)

| File | Change |
|------|--------|
| `web-components/VsFilterSettings.tsx` | **NEW** Admin config page |

## New Configuration Options

### EncodingOptions (Backend)

```csharp
// Enable/disable the VS Filter pipeline
public bool EnableVsFilterPipeline { get; set; }

// Preset selection
public string VsFilterPreset { get; set; }  // "anime-upscaled", "anime-interpolated", "custom"

// Custom VapourSynth script
public string VsFilterCustomScript { get; set; }

// AI Model settings
public string VsUpscaleModel { get; set; }        // "realesr-animevideov3", etc.
public string VsInterpolationModel { get; set; }   // "rife-4.25", etc.

// Output settings
public int VsTargetWidth { get; set; }            // Default: 3840
public int VsTargetHeight { get; set; }           // Default: 2160
public int VsTargetFpsNum { get; set; }           // Default: 60000
public int VsTargetFpsDen { get; set; }           // Default: 1001 (~60fps)
public string VsPixelFormat { get; set; }         // Default: "YUV420P10"

// Performance settings
public int VsThreads { get; set; }                // Default: 0 (auto)
public string VsEncoderArgs { get; set; }         // Custom FFmpeg args
```

## API Endpoints

### GET /EncodingOptions/VsFilter
Returns current VS Filter configuration.

### POST /EncodingOptions/VsFilter
Updates VS Filter configuration.

## Usage

### 1. Build FFmpeg with VapourSynth support

```bash
git clone https://github.com/efschu/FFmpeg.git
cd FFmpeg
./build_ffmpeg_vs.sh --enable-gpl
```

### 2. Install VapourSynth plugins

```bash
# Core VapourSynth
pip install vapoursynth numpy

# AI Upscaling plugins
pip install vs-realesrgan vsrife

# TensorRT for GPU acceleration (optional)
# See: https://github.com/AmusementClub/vstrt
```

### 3. Enable in Jellyfin

1. Go to **Dashboard → Transcoding → 4K×2 VapourSynth Filter**
2. Enable the pipeline
3. Select preset (e.g., "Anime Interpolated" for 2x upscale + 60fps)
4. Save settings

### 4. Use in Player

When enabled, content will automatically be processed through the VS Filter pipeline when:
- Video resolution is ≥ 1920px
- Client requests transcoding

## Presets

| Preset | Upscaling | Interpolation | Target FPS |
|--------|-----------|---------------|------------|
| `anime-upscaled` | 2x Real-ESRGAN | None | Original |
| `anime-interpolated` | 2x Real-ESRGAN | RIFE | ~60fps |
| `upscale-only` | 2x Real-ESRGAN | None | Original |
| `custom` | User script | User script | User script |

## Example Custom Script

```python
import vapoursynth as vs
core = vs.core

# Load TensorRT plugin
core.std.LoadPlugin('/usr/lib/vapoursynth/libvstrt.so')

# Read input
clip = core.lsmas.Source('/path/to/video.mkv')

# Upscale with Real-ESRGAN
clip = clip.resize.Bicubic(format=vs.RGBS, matrix_in_s='709')
clip = core.trt.Model(clip, engine='/models/upscaler.engine')

# Frame interpolation
clip = core.rife.RIFE(clip, model='rife.25', factor_num=60000, factor_den=1001, trt=True)

# Convert output
clip = clip.resize.Bicubic(format=vs.YUV420P10, matrix_s='709')

clip.set_output()
```

## Building

### Prerequisites

- .NET 8+ SDK
- FFmpeg built with `--enable-vapoursynth`
- VapourSynth SDK headers

### Build Jellyfin

```bash
cd jellyfin
dotnet build
```

## Docker Integration

```dockerfile
FROM jellyfin/jellyfin:latest

# Install FFmpeg with VapourSynth support
COPY --from=efschu/ffmpeg-vs:latest /usr/local/bin/ffmpeg /usr/local/bin/ffmpeg

# Install VapourSynth plugins
RUN pip install vs-realesrgan vsrife
```

## Troubleshooting

### FFmpeg doesn't have VapourSynth support
```
Error: No filter named 'vapourSynth'
```
**Solution**: Rebuild FFmpeg with `--enable-vapoursynth`

### TensorRT plugin not found
```
Error: No property with the given name found: trt
```
**Solution**: Install vstrt plugin or remove TensorRT options from script

### Out of memory
Reduce output resolution or disable frame interpolation.

## Credits

- VapourSynth: https://vapoursynth.com
- Real-ESRGAN: https://github.com/xinntao/Real-ESRGAN
- RIFE: https://github.com/megvii-research/ECCV2022-RIFE
- FFmpeg VS Filter: https://github.com/efschu/FFmpeg
