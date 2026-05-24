using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using MediaBrowser.Model.Configuration;

namespace MediaBrowser.MediaEncoding.Transcoding;

/// <summary>
/// Manages the FFmpeg -> vspipe -> FFmpeg transcoding pipeline using named pipes.
/// </summary>
public sealed class VspipePipeline : IDisposable
{
    private readonly string _transcodeTempPath;
    private readonly string _logDirectory;
    private readonly List<string> _namedPipes = new();

    private Process? _ffmpegDecoder;
    private Process? _vspipeProcess;
    private Process? _ffmpegEncoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="VspipePipeline"/> class.
    /// </summary>
    /// <param name="transcodeTempPath">The transcoding temp path.</param>
    /// <param name="logDirectory">The log directory path.</param>
    public VspipePipeline(string transcodeTempPath, string logDirectory)
    {
        _transcodeTempPath = transcodeTempPath;
        _logDirectory = logDirectory;
    }

    /// <summary>
    /// Gets the FFmpeg encoder process.
    /// </summary>
    public Process? FfmpegEncoder => _ffmpegEncoder;

    /// <summary>
    /// Disposes the pipeline and cleans up resources.
    /// </summary>
    public void Dispose()
    {
        StopProcesses();
        CleanupPipes();
    }

    private void StopProcesses()
    {
        foreach (var p in new[] { _ffmpegDecoder, _vspipeProcess, _ffmpegEncoder })
        {
            if (p is not null && !p.HasExited)
            {
                p.Kill();
                p.WaitForExit();
                p.Dispose();
            }
        }
    }

    /// <summary>
    /// Starts the vspipe pipeline.
    /// </summary>
    /// <param name="sourcePath">Source media path.</param>
    /// <param name="outputPath">Output HLS playlist path.</param>
    /// <param name="encoderPath">FFmpeg encoder path.</param>
    /// <param name="encodingOptions">Encoding options.</param>
    /// <param name="videoStreamIndex">Video stream index.</param>
    /// <param name="audioStreamIndex">Audio stream index.</param>
    /// <param name="segmentLength">Segment length in seconds.</param>
    /// <param name="segmentContainer">Segment container format.</param>
    /// <param name="videoCodec">Output video codec.</param>
    /// <param name="audioCodec">Output audio codec.</param>
    /// <param name="videoBitrate">Video bitrate.</param>
    /// <param name="audioBitrate">Audio bitrate.</param>
    /// <param name="width">Source width.</param>
    /// <param name="height">Source height.</param>
    /// <param name="pixelFormat">Pixel format.</param>
    /// <param name="inputFramerate">Input framerate.</param>
    /// <param name="hardwareAccelerationType">Hardware acceleration type.</param>
    /// <param name="hwDevice">Hardware device path.</param>
    public void Start(
        string sourcePath,
        string outputPath,
        string encoderPath,
        EncodingOptions encodingOptions,
        int videoStreamIndex,
        int audioStreamIndex,
        int segmentLength,
        string segmentContainer,
        string videoCodec,
        string audioCodec,
        int? videoBitrate,
        int? audioBitrate,
        int width,
        int height,
        string pixelFormat,
        double? inputFramerate,
        string? hardwareAccelerationType,
        string? hwDevice)
    {
        var baseName = Path.GetFileNameWithoutExtension(outputPath);

        var pipeVideoRaw = CreateNamedPipe(Path.Combine(_transcodeTempPath, $"{baseName}-video-raw"));
        var pipeAudioRaw = CreateNamedPipe(Path.Combine(_transcodeTempPath, $"{baseName}-audio-raw"));
        var pipeVideoUpscaled = CreateNamedPipe(Path.Combine(_transcodeTempPath, $"{baseName}-video-upscaled"));

        var scriptPath = GenerateVspipeScript(
            encodingOptions,
            pipeVideoRaw,
            width,
            height,
            pixelFormat,
            inputFramerate);

        var logPrefix = "FFmpeg.4kx2-";
        var logFilePath = Path.Combine(
            _logDirectory,
            $"{logPrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString()[..8]}.log");

        var videoArgs = BuildDecoderVideoArgs(
            sourcePath,
            videoStreamIndex,
            pipeVideoRaw,
            width,
            height,
            pixelFormat,
            hardwareAccelerationType,
            hwDevice);

        var audioArgs = BuildDecoderAudioArgs(
            sourcePath,
            audioStreamIndex,
            pipeAudioRaw,
            audioCodec);

        var decoderArgs = $"{videoArgs} {audioArgs} -an -c:v:0 copy -c:a:0 copy -y /dev/null";

        var vspipeArgs = $"-p {scriptPath} {pipeVideoUpscaled}";

        var encoderArgs = BuildEncoderArgs(
            pipeVideoUpscaled,
            pipeAudioRaw,
            outputPath,
            encoderPath,
            videoCodec,
            audioCodec,
            videoBitrate,
            audioBitrate,
            segmentLength,
            segmentContainer,
            encodingOptions);

        var logContent = $"=== 4kx2 Vspipe Pipeline ===\n\n";
        logContent += $"Source: {sourcePath}\n";
        logContent += $"Output: {outputPath}\n";
        logContent += $"Target: {encodingOptions.VspipeTargetWidth}x{encodingOptions.VspipeTargetHeight} @ {encodingOptions.VspipeTargetFpsNum}/{encodingOptions.VspipeTargetFpsDen}\n";
        logContent += $"Upscale model: {encodingOptions.VspipeUpscaleModel}\n";
        logContent += $"Interpolation model: {encodingOptions.VspipeInterpolationModel}\n\n";
        logContent += $"[Decoder] {encoderPath} {decoderArgs}\n\n";
        logContent += $"[Vspipe] {encodingOptions.VspipePath} {vspipeArgs}\n\n";
        logContent += $"[Encoder] {encoderPath} {encoderArgs}\n";

        File.WriteAllText(logFilePath, logContent);

        _ffmpegDecoder = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = encoderPath,
                Arguments = decoderArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _vspipeProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = encodingOptions.VspipePath,
                Arguments = vspipeArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _ffmpegEncoder = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = encoderPath,
                Arguments = encoderArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _ffmpegDecoder.Start();
        _vspipeProcess.Start();
        _ffmpegEncoder.Start();
    }

    private string CreateNamedPipe(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            System.Diagnostics.Process.Start("mkfifo", path).WaitForExit();
        }

        _namedPipes.Add(path);
        return path;
    }

    private void CleanupPipes()
    {
        foreach (var pipe in _namedPipes)
        {
            try
            {
                if (File.Exists(pipe))
                {
                    File.Delete(pipe);
                }
            }
            catch
            {
            }
        }

        _namedPipes.Clear();
    }

    private string GenerateVspipeScript(
        EncodingOptions encodingOptions,
        string inputPipe,
        int sourceWidth,
        int sourceHeight,
        string pixelFormat,
        double? inputFramerate)
    {
        var targetWidth = encodingOptions.VspipeTargetWidth;
        var targetHeight = encodingOptions.VspipeTargetHeight;
        var fpsNum = encodingOptions.VspipeTargetFpsNum;
        var fpsDen = encodingOptions.VspipeTargetFpsDen;
        var upscaleModel = encodingOptions.VspipeUpscaleModel;
        var interpModel = encodingOptions.VspipeInterpolationModel;

        var fps = inputFramerate ?? 30.0;
        var fpsStr = fps.ToString(CultureInfo.InvariantCulture);

        var script = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(encodingOptions.VspipeScriptPath))
        {
            return encodingOptions.VspipeScriptPath;
        }

        script.AppendLine("import vapoursynth as vs");
        script.AppendLine("from vapoursynth import core");
        script.AppendLine("import vsrawsource as raws");
        script.AppendLine();
        script.AppendLine($"# Read raw video from named pipe via vsrawsource");
        script.AppendLine($"clip = raws.Source(\"{inputPipe}\", width={sourceWidth}, height={sourceHeight}, fmt=\"{pixelFormat}\", fpsnum={int.Parse(fpsStr) * fpsDen}, fpsden={fpsDen})");
        script.AppendLine();

        if (encodingOptions.VspipeUpscaleModel.Contains("realesr", StringComparison.OrdinalIgnoreCase))
        {
            script.AppendLine("# Upscale with Real-ESRGAN");
            script.AppendLine($"from vsrealesrgan import realesrgan");
            script.AppendLine($"upscaled = realesrgan(clip, model=\"{upscaleModel}\", scale=2)");
            script.AppendLine();
            script.AppendLine("# Scale to target resolution if needed");
            script.AppendLine($"upscaled = core.resize.Bicubic(upscaled, width={targetWidth}, height={targetHeight})");
            script.AppendLine();
            script.AppendLine($"clip = upscaled");
        }

        if (encodingOptions.VspipeInterpolationModel.Contains("rife", StringComparison.OrdinalIgnoreCase))
        {
            script.AppendLine("# Frame interpolation with RIFE");
            script.AppendLine($"from vsrife import rife");
            script.AppendLine($"clip = rife(clip, model=\"{interpModel}\", factor_num={fpsNum}, factor_den={fpsDen}, trt=True, sc=True)");
            script.AppendLine();
        }

        script.AppendLine("# Convert to output format");
        script.AppendLine($"clip = core.resize.Bicubic(clip, format=vs.{pixelFormat.Replace("YUV420P8", "YUV420P8").Replace("YUV420P10", "YUV420P10")}, matrix_s=\"709\")");
        script.AppendLine();
        script.AppendLine("clip.set_output()");

        var scriptPath = Path.Combine(_transcodeTempPath, $"vspipe-script-{Guid.NewGuid()}.vpy");
        File.WriteAllText(scriptPath, script.ToString());

        return scriptPath;
    }

    private string BuildDecoderVideoArgs(
        string sourcePath,
        int videoStreamIndex,
        string outputPipe,
        int width,
        int height,
        string pixelFormat,
        string? hardwareAccelerationType,
        string? hwDevice)
    {
        var args = new StringBuilder();

        if (!string.IsNullOrEmpty(hardwareAccelerationType) && hardwareAccelerationType != "none")
        {
            args.Append($"-hwaccel {hardwareAccelerationType}");

            if (!string.IsNullOrEmpty(hwDevice))
            {
                args.Append($" -hwaccel_device {hwDevice}");
            }
        }

        args.Append($" -i \"{sourcePath}\"");
        args.Append($" -map 0:v:{videoStreamIndex}");
        args.Append($" -vf \"scale={width}:{height},format={pixelFormat.ToLowerInvariant().Replace("yuv420p8", "yuv420p").Replace("yuv420p10", "yuv420p10le")}\"");
        args.Append($" -f rawvideo -pix_fmt {pixelFormat.ToLowerInvariant().Replace("yuv420p8", "yuv420p").Replace("yuv420p10", "yuv420p10le")}");
        args.Append($" \"{outputPipe}\"");

        return args.ToString();
    }

    private string BuildDecoderAudioArgs(
        string sourcePath,
        int audioStreamIndex,
        string outputPipe,
        string audioCodec)
    {
        return $" -map 0:a:{audioStreamIndex} -c:a copy -f adts \"{outputPipe}\"";
    }

    private string BuildEncoderArgs(
        string videoPipe,
        string audioPipe,
        string outputPath,
        string encoderPath,
        string videoCodec,
        string audioCodec,
        int? videoBitrate,
        int? audioBitrate,
        int segmentLength,
        string segmentContainer,
        EncodingOptions encodingOptions)
    {
        var args = new StringBuilder();

        var ext = segmentContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? ".m4s" : ".ts";
        var initName = segmentContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(outputPath) + "-init.m4s"
            : string.Empty;

        args.Append($" -f rawvideo -pix_fmt yuv420p10le -s {encodingOptions.VspipeTargetWidth}x{encodingOptions.VspipeTargetHeight} -r {encodingOptions.VspipeTargetFpsNum}/{encodingOptions.VspipeTargetFpsDen} -i \"{videoPipe}\"");
        args.Append($" -i \"{audioPipe}\"");
        args.Append($" -map 0:v -map 1:a");
        args.Append($" -c:v {videoCodec}");
        args.Append($" -c:a {audioCodec}");

        if (videoBitrate.HasValue)
        {
            args.Append($" -b:v {videoBitrate.Value} -maxrate {videoBitrate.Value} -bufsize {videoBitrate.Value * 2}");
        }

        if (audioBitrate.HasValue)
        {
            args.Append($" -ab {audioBitrate.Value}");
        }

        args.Append($" -f hls");
        args.Append($" -hls_time {segmentLength}");
        args.Append($" -hls_segment_type {(segmentContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? "fmp4" : "mpegts")}");

        if (!string.IsNullOrEmpty(initName))
        {
            args.Append($" -hls_fmp4_init_filename \"{initName}\"");
        }

        var segName = Path.GetFileNameWithoutExtension(outputPath);
        args.Append($" -hls_segment_filename \"{Path.GetDirectoryName(outputPath)}/{segName}%d{ext}\"");
        args.Append($" -hls_list_size 0");
        args.Append($" -max_muxing_queue_size {encodingOptions.MaxMuxingQueueSize}");
        args.Append($" -copyts -avoid_negative_ts disabled");
        args.Append($" -y \"{outputPath}\"");

        return args.ToString();
    }
}
