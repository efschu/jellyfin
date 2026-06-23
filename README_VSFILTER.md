# Jellyfin mit FFmpeg VapourSynth-Filter-Pipeline

Dieser Branch fügt eine **VapourSynth-Filter-Pipeline** zu Jellyfin hinzu, die AI-gestütztes Upscaling und Frame-Interpolation über FFmpeg's eingebauten `-vf vapoursynth=...` Filter ermöglicht.

## Übersicht

### Architektur

```
┌────────────────────────────────────────────────────────────┐
│ Standard-Transcoding                                       │
│                                                            │
│  Input → FFmpeg → Standard Encoding → HLS                 │
└────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────┐
│ VS-Filter-Pipeline (NEU in diesem Branch)                  │
│                                                            │
│  Input → FFmpeg mit -vf vapoursynth=...                    │
│              │                                             │
│              ▼                                             │
│        ┌──────────────┐                                   │
│        │ VapourSynth  │                                   │
│        │   Script     │ ← AI-Upscaling (Real-ESRGAN)      │
│        │  (.vpy file) │ ← Frame-Interpolation (RIFE)      │
│        └──────────────┘ ← TensorRT GPU-Beschleunigung      │
│              │                                             │
│              ▼                                             │
│         FFmpeg → HLS                                      │
└────────────────────────────────────────────────────────────┘
```

**Vorteile:**
- ✅ **Single FFmpeg-Prozess** (kein externer `vspipe`)
- ✅ **In-Process VapourSynth** (niedrigere Latenz)
- ✅ **AI-Upscaling** mit Real-ESRGAN
- ✅ **Frame-Interpolation** mit RIFE
- ✅ **TensorRT** für GPU-Beschleunigung

## Anforderungen

### 1. FFmpeg mit VapourSynth-Support

**Wichtig:** Du brauchst den speziellen FFmpeg-Fork mit VapourSynth-Filter:
- https://github.com/efschu/FFmpeg (`master` Branch)
- Oder FFmpeg mit `--enable-vapoursynth` selbst gebaut

Der Standard-FFmpeg von Jellyfin funktioniert **NICHT** für diese Pipeline.

### 2. VapourSynth Python-Pakete

```bash
# VapourSynth-Grundinstallation
apt install python3 python3-pip
pip install numpy vapoursynth

# Optional: AI-Modelle
pip install vs-realesrgan vsrife
```

### 3. TensorRT (Optional, für GPU-Beschleunigung)

Für NVIDIA-GPU-Beschleunigung:
- https://github.com/AmusementClub/vstrt
- CUDA Toolkit
- TensorRT

## Konfigurations-Optionen

Alle Optionen sind in `EncodingOptions.cs` definiert:

| Option | Default | Beschreibung |
|--------|---------|--------------|
| `EnableVsFilterPipeline` | `false` | Pipeline aktivieren/deaktivieren |
| `VsFilterPreset` | `anime-upscaled` | Preset: `anime-upscaled`, `anime-interpolated`, `custom` |
| `VsFilterCustomScript` | leer | Pfad zu eigenem VapourSynth-Script |
| `VsUpscaleModel` | `realesr-animevideov3` | AI-Upscaling-Modell |
| `VsInterpolationModel` | `rife-4.25` | Frame-Interpolation-Modell |
| `VsTargetWidth` | `3840` | Ziel-Auflösung Breite |
| `VsTargetHeight` | `2160` | Ziel-Auflösung Höhe |
| `VsTargetFpsNum` | `60000` | FPS-Zähler (~60fps) |
| `VsTargetFpsDen` | `1001` | FPS-Nenner (NTSC) |
| `VsPixelFormat` | `YUV420P10` | Output-Pixel-Format |
| `VsThreads` | `0` | Threads (0 = auto) |
| `VsEncoderArgs` | leer | Custom FFmpeg-Encoder-Args |

## Presets

| Preset | Upscaling | Interpolation | Ziel-FPS |
|--------|-----------|---------------|----------|
| `anime-upscaled` | 2× Real-ESRGAN | Keine | Original |
| `anime-interpolated` | 2× Real-ESRGAN | RIFE | ~60fps |
| `custom` | User-Script | User-Script | User-Script |

## Dateien die geändert wurden

### Backend (C#)
- `MediaBrowser.Model/Configuration/EncodingOptions.cs` - Neue VS-Filter-Optionen
- `MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs` - VS-Filter-Routing
- `MediaBrowser.MediaEncoding/Transcoding/VsFilterPipeline.cs` - **NEU**: Pipeline-Implementation
- `Jellyfin.Api/Controllers/VsFilterController.cs` - **NEU**: REST API

### Frontend (TypeScript/React)
- `web-components/VsFilterSettings.tsx` - **NEU**: Admin-Config-Page

### Build
- `Dockerfile` - Multi-Stage-Build mit FFmpeg + VapourSynth

## Architektur im Detail

### VsFilterPipeline.cs

Diese Klasse:
1. **Erkennt** ob `EnableVsFilterPipeline` aktiviert ist
2. **Generiert** ein dynamisches VapourSynth-Script (.vpy)
3. **Startet** FFmpeg mit `vapoursynth=file=script.vpy`
4. **Verwaltet** das FFmpeg-Prozess-Handle

### TranscodeManager.cs

Im `StartFfMpeg()`:
```csharp
// Prüfe ob VS-Filter-Modus
if (encodingOptions.EnableVsFilterPipeline && state.VideoRequest != null)
{
    return await StartVsFilterFfMpegAsync(...);
}
else
{
    return await StartStandardFfMpegAsync(...);
}
```

## Verwendung

### 1. FFmpeg bauen

```bash
git clone https://github.com/efschu/FFmpeg.git
cd FFmpeg
./configure --enable-vapoursynth
make -j4 && make install
```

### 2. Docker-Image bauen

```bash
git clone https://github.com/efschu/jellyfin.git
cd jellyfin
git checkout feature/vsfilter
docker build -t jellyfin-vsfilter -f Dockerfile .
```

### 3. Container starten

```bash
docker run -d \
  --name jellyfin \
  -p 8096:8096 \
  -p 8920:8920 \
  -v /path/to/config:/config \
  -v /path/to/media:/media \
  --privileged \
  --gpus all \
  jellyfin-vsfilter
```

### 4. Konfiguration im Web-UI

1. Gehe zu **Dashboard → Transcoding → 4K×2 VapourSynth Filter**
2. Aktiviere die Pipeline
3. Wähle ein Preset
4. Speichere

## Custom VapourSynth-Script

Wenn du `VsFilterPreset = "custom"` setzt und `VsFilterCustomScript` auf eine `.vpy`-Datei verweist, wird dieses Script verwendet.

### Beispiel: Real-ESRGAN + RIFE

```python
import vapoursynth as vs
core = vs.core

# TensorRT-Plugin laden
try:
    core.std.LoadPlugin('/usr/lib/vapoursynth/libvstrt.so')
except:
    pass

# Input laden (FFmpeg übergibt das file Argument)
# Wir müssen den Pfad selbst parsen
import sys
input_path = None
# In realer Nutzung wird der file= Parameter an VapourSynth weitergegeben

# Standard-Pipeline
clip = core.std.BlankClip(format=vs.YUV420P8, width=1920, height=1080, length=5)

# Real-ESRGAN Upscaling
clip = core.resize.Bicubic(clip, format=vs.RGBS, matrix_in_s='709')
clip = core.trt.Model(clip, engine='/models/realesr-anime.engine')
clip = core.resize.Bicubic(clip, width=3840, height=2160, format=vs.RGBS, matrix_s='709')

# RIFE Frame-Interpolation
clip = core.rife.RIFE(clip, model='rife.25', factor_num=60000, factor_den=1001, trt=True)

# Output-Format
clip = core.resize.Bicubic(clip, format=vs.YUV420P10, matrix_s='709')
clip.set_output()
```

## API-Endpoints

### `GET /EncodingOptions/VsFilter`
Liefert die aktuelle VS-Filter-Konfiguration.

### `POST /EncodingOptions/VsFilter`
Aktualisiert die VS-Filter-Konfiguration.

Beispiel:
```bash
curl -X POST http://localhost:8096/EncodingOptions/VsFilter \
  -H "Authorization: MediaBrowser Token=YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "EnableVsFilterPipeline": true,
    "VsFilterPreset": "anime-interpolated",
    "VsUpscaleModel": "realesr-animevideov3",
    "VsInterpolationModel": "rife-4.25",
    "VsTargetWidth": 3840,
    "VsTargetHeight": 2160
  }'
```

## Troubleshooting

### "No filter named 'vapoursynth'"
FFmpeg hat keinen VapourSynth-Support. Baue FFmpeg neu mit `--enable-vapoursynth`:
```bash
git clone https://github.com/efschu/FFmpeg.git
cd FFmpeg
./configure --enable-vapoursynth
make -j4 && make install
```

### "libvapoursynth.so: cannot open shared object file"
VapourSynth ist nicht installiert oder die Library ist nicht im Library-Pfad:
```bash
export LD_LIBRARY_PATH=/usr/local/lib:$LD_LIBRARY_PATH
ldconfig
```

### "No module named 'vsrealesrgan'"
Plugin installieren:
```bash
pip install vs-realesrgan
```

### Out of Memory
- Ziel-Auflösung reduzieren (z.B. 1920×1080 statt 3840×2160)
- Frame-Interpolation deaktivieren
- GPU statt CPU verwenden (TensorRT)

### Performance ist schlecht
- NVIDIA GPU + TensorRT verwenden
- Kleinere Modelle wählen (z.B. realesrgan-x4plus statt animevideov3)
- Threads auf CPU-Cores setzen (`VsThreads = 0` für auto)

## Verwandte Projekte

- **FFmpeg-Fork** mit VapourSynth-Support: https://github.com/efschu/FFmpeg
- **Docker-Image** (vorher gebaut): `efschu/jellyfin-vsfilter:latest`

## Credits

- [VapourSynth](https://vapoursynth.com)
- [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN)
- [RIFE](https://github.com/megvii-research/ECCV2022-RIFE)
- [Jellyfin](https://jellyfin.org)
- [FFmpeg](https://ffmpeg.org)

## Lizenz

Jellyfin ist unter GPL v2 lizenziert. Siehe [LICENSE](LICENSE) für Details.
