# AGENTS.md - Development Guide für Jellyfin VS-Filter-Fork

Diese Datei richtet sich an Entwickler und AI-Agents, die an diesem Jellyfin-Fork weiterarbeiten möchten.

## Projekt-Übersicht

Dieser Branch (`feature/vsfilter`) fügt Jellyfin eine **VapourSynth-Filter-Pipeline** hinzu. Sie nutzt FFmpeg's VapourSynth-Filter (`-vf vapoursynth=file=script.vpy`) für AI-gestütztes Video-Processing.

### Voraussetzungen

1. **Spezieller FFmpeg-Fork:** https://github.com/efschu/FFmpeg
   - Hat den `vf_vapoursynth` Filter
   - **Standard-FFmpeg von Jellyfin funktioniert NICHT**
2. **VapourSynth R73+** mit Python-Bindings
3. **AI-Modelle** (optional): Real-ESRGAN, RIFE, etc.

### Datenfluss

```
┌─────────────────────────────────────────────────────────────┐
│ Jellyfin-Player (Web/Java/etc.)                             │
│                ↓ HTTP/HLS-Request                           │
│ TranscodeManager.StartFfMpeg()                              │
│                ↓                                             │
│ Erkennt: EnableVsFilterPipeline == true?                    │
│   ├── Nein: StartStandardFfMpegAsync()                       │
│   └── Ja:  StartVsFilterFfMpegAsync()                       │
│                ↓                                             │
│ VsFilterPipeline.Start()                                    │
│   1. Generiere .vpy-Script                                  │
│   2. Starte FFmpeg mit -vf vapoursynth=file=script.vpy        │
│                ↓                                             │
│ FFmpeg → VapourSynth-Script → AI-Processing → HLS-Output    │
└─────────────────────────────────────────────────────────────┘
```

## Code-Struktur

### Relevante Dateien

| Datei | Zweck | Ändern wenn... |
|-------|-------|----------------|
| `MediaBrowser.Model/Configuration/EncodingOptions.cs` | Alle Config-Optionen | Neue Optionen hinzufügen |
| `MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs` | FFmpeg-Start-Logik | Routing ändern |
| `MediaBrowser.MediaEncoding/Transcoding/VsFilterPipeline.cs` | VS-Pipeline-Implementation | Script-Generierung ändern |
| `Jellyfin.Api/Controllers/VsFilterController.cs` | REST-API | Neue API-Endpoints |
| `web-components/VsFilterSettings.tsx` | Admin-UI | UI-Felder ändern |
| `Dockerfile` | Multi-Stage-Build | Build-Optionen ändern |

### EncodingOptions.cs

Alle VS-Filter-Optionen sind hier definiert:

```csharp
public bool EnableVsFilterPipeline { get; set; }
public string VsFilterPreset { get; set; } = "anime-upscaled";
public string VsFilterCustomScript { get; set; } = string.Empty;
public string VsUpscaleModel { get; set; } = "realesr-animevideov3";
public string VsInterpolationModel { get; set; } = "rife-4.25";
public int VsTargetWidth { get; set; } = 3840;
public int VsTargetHeight { get; set; } = 2160;
public int VsTargetFpsNum { get; set; } = 60000;
public int VsTargetFpsDen { get; set; } = 1001;
public string VsPixelFormat { get; set; } = "YUV420P10";
public int VsThreads { get; set; }
public string VsEncoderArgs { get; set; } = string.Empty;
```

**Wichtig:** Diese Optionen müssen auch in `VsFilterOptions` (in `VsFilterController.cs`) gespiegelt werden!

### TranscodeManager.cs

Im `StartFfMpeg()` wird entschieden, ob VS-Filter verwendet wird:

```csharp
public async Task<TranscodingJob> StartFfMpeg(...)
{
    var encodingOptions = _serverConfigurationManager.GetEncodingOptions();
    
    if (encodingOptions.EnableVsFilterPipeline && state.VideoRequest != null)
    {
        return await StartVsFilterFfMpegAsync(...);
    }
    else
    {
        return await StartStandardFfMpegAsync(...);
    }
}
```

### VsFilterPipeline.cs

Diese Klasse:
1. **Erstellt** ein dynamisches VapourSynth-Script
2. **Startet** FFmpeg mit dem Script
3. **Verwaltet** den FFmpeg-Prozess

#### Wichtige Methoden

- `Start(...)` - Startet die Pipeline
- `GenerateVsScript(...)` - Erstellt das .vpy-Script
- `BuildFfmpegArgs(...)` - Baut die FFmpeg-Command-Line

#### Dynamisches Script-Generierung

```csharp
private string GenerateVsScript(string sourcePath, EncodingOptions options, int videoStreamIndex)
{
    // Custom-Script verwenden, wenn vorhanden
    if (!string.IsNullOrWhiteSpace(options.VsFilterCustomScript))
        return options.VsFilterCustomScript;
    
    // Sonst: dynamisches Script generieren
    var script = new StringBuilder();
    script.AppendLine("import vapoursynth as vs");
    script.AppendLine("from vapoursynth import core");
    
    // Preset-basiert: Real-ESRGAN + RIFE
    if (preset.Contains("upscale"))
    {
        script.AppendLine("# Real-ESRGAN Upscaling");
        script.AppendLine("clip = core.resize.Bicubic(clip, format=vs.RGBS)");
        script.AppendLine($"clip = core.trt.Model(clip, model='{model}')");
    }
    
    if (preset.Contains("interpolat"))
    {
        script.AppendLine("# RIFE Frame Interpolation");
        script.AppendLine($"clip = core.rife.RIFE(clip, factor_num={fpsNum}, factor_den={fpsDen}, trt=True)");
    }
    
    return scriptPath;
}
```

### VsFilterController.cs

REST-API für die Optionen:

```csharp
[Route("EncodingOptions/VsFilter")]
[ApiController]
public class VsFilterController : ControllerBase
{
    [HttpGet]
    public ActionResult<VsFilterOptions> GetVsFilterOptions() { ... }
    
    [HttpPost]
    public async Task<ActionResult> UpdateVsFilterOptions([FromBody] VsFilterOptions options) { ... }
}
```

## Wichtige Jellyfin-Konzepte

### TranscodeManager

Der `TranscodeManager` ist verantwortlich für:
- Starten von FFmpeg-Prozessen
- Tracking von Transcoding-Jobs
- Throttling/Cleanup

Wichtige Methoden:
- `StartFfMpeg()` - Entry-Point
- `KillTranscodingJobs()` - Cleanup
- `ReportTranscodingProgress()` - Status-Updates

### StreamState

Enthält alle Informationen über den aktuellen Stream:
- `VideoStream`, `AudioStream`, `SubtitleStream`
- `MediaPath` - Pfad zur Quelldatei
- `OutputPath` - Wo HLS-Output geschrieben wird
- `VideoRequest` - User-Einstellungen für Video

### IMediaEncoder

Interface für FFmpeg:
- `EncoderPath` - Pfad zur FFmpeg-Binary
- `EncoderVersion` - FFmpeg-Version
- `ProbePath` - Pfad zu ffprobe

## Build-Befehle

### Standard-Build

```bash
dotnet build
```

### Docker-Build

```bash
docker build -t jellyfin-vsfilter -f Dockerfile .
```

## Entwicklung-Workflow

### Neues Config-Option hinzufügen

1. **EncodingOptions.cs:** Property hinzufügen
   ```csharp
   public string NeueOption { get; set; } = "default";
   ```

2. **VsFilterController.cs:** VsFilterOptions erweitern
   ```csharp
   public class VsFilterOptions
   {
       // ... existing
       public string NeueOption { get; set; } = "default";
   }
   ```

3. **API-Mapping:** In `GetVsFilterOptions()` und `UpdateVsFilterOptions()`

4. **Frontend:** `web-components/VsFilterSettings.tsx` anpassen

### Neues VapourSynth-Modell unterstützen

Im dynamischen Script-Generator (`GenerateVsScript`):
```csharp
if (options.VsUpscaleModel == "neues-modell")
{
    script.AppendLine($"clip = core.neues_modell.Process(clip, ...)");
}
```

## Häufige Probleme und Lösungen

### Problem: FFmpeg findet `vapoursynth` Filter nicht

**Ursache:** Standard-Jellyfin-FFmpeg hat diesen Filter nicht.

**Lösung:** In der `Dockerfile` muss der efschu/FFmpeg-Fork gebaut werden:
```dockerfile
RUN git clone --depth 1 --branch master https://github.com/efschu/FFmpeg.git ffmpeg
```

### Problem: VapourSynth Library nicht gefunden

**Ursache:** `libvapoursynth.so` fehlt im Runtime-Image.

**Lösung:** In `Dockerfile` muss VapourSynth R73 gebaut und kopiert werden:
```dockerfile
RUN git clone --depth 1 --branch R73 https://github.com/vapoursynth/vapoursynth.git
RUN ./configure && make -j4 && make install
```

### Problem: Python-Versions-Mismatch

**Ursache:** VapourSynth ist gegen Python 3.10 gebaut, aber Runtime hat Python 3.11.

**Lösung:** Entweder VapourSynth gegen die Runtime-Python bauen, oder Python 3.10 in Runtime installieren.

## Testing

```bash
# 1. FFmpeg manuell testen
cat > /tmp/test.vpy << 'EOF'
import vapoursynth as vs
core = vs.core
clip = core.std.BlankClip(format=vs.YUV420P8, width=320, height=240, length=5)
clip.set_output()
EOF

ffmpeg -i test.mp4 -vf "vapoursynth=file=/tmp/test.vpy" -frames:v 1 -f null -

# 2. Jellyfin-API testen
curl -X GET http://localhost:8096/EncodingOptions/VsFilter \
  -H "Authorization: MediaBrowser Token=YOUR_TOKEN"
```

## Coding-Standards

1. **Naming:** PascalCase für C# Klassen/Methoden, camelCase für Properties
2. **Async:** Verwende `async/await` für alle I/O-Operationen
3. **Error-Handling:** Verwende `ILogger` für Logging
4. **Resources:** Immer in `IDisposable.Dispose()` freigeben
5. **Config-Options:** Jede neue Option in 3 Dateien: EncodingOptions, VsFilterOptions, API-Controller

## Wichtige Links

- **FFmpeg-Fork:** https://github.com/efschu/FFmpeg
- **VapourSynth:** https://www.vapoursynth.com
- **Jellyfin-Codebase:** https://github.com/jellyfin/jellyfin
- **Jellyfin-Transcoding-Docs:** https://jellyfin.org/docs/general/server/transcoding

## Bekannte Einschränkungen

- **Performance:** AI-Modelle sind CPU/GPU-intensiv
- **Memory:** Große Buffers nötig für hohe Auflösungen
- **Compatibility:** Nur VapourSynth R73 (API v4.0) offiziell unterstützt
- **Latenz:** Die Pipeline hat zusätzliche Latenz durch AI-Processing

## Nächste Schritte / Roadmap

- [ ] Performance-Optimierungen (z.B. async script loading)
- [ ] Mehr AI-Modelle (SwinIR, Real-ESRGAN x4, etc.)
- [ ] Bessere Fehlerbehandlung
- [ ] UI für Live-Statistiken
- [ ] Profile-basierte Konfiguration pro User
- [ ] WebSocket für Pipeline-Status

## Kontakt

Bei Fragen oder Problemen: Erstelle ein Issue auf GitHub.
