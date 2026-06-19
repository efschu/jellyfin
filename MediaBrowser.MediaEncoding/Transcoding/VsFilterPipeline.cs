using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using MediaBrowser.Model.Configuration;

namespace MediaBrowser.MediaEncoding.Transcoding;

/// <summary>
/// Manages the FFmpeg VapourSynth filter pipeline using the built-in VS filter.
/// This replaces the old vspipe-based pipeline with a single FFmpeg process.
/// </summary>
public sealed class VsFilterPipeline : IDisposable
{
    private readonly string _transcodeTempPath;
    private readonly string _logDirectory;

    private Process? _ffmpegProcess;

    /// <summary>
    /// Initializes a new instance of the <see cref="VsFilterPipeline"/> class.
    /// </summary>
    /// <param name="transcodeTempPath">The transcoding temp path.</param>
    /// <param name="logDirectory">The log directory path.</param>
    public VsFilterPipeline(string transcodeTempPath, string logDirectory)
    {
        _transcodeTempPath = transcodeTempPath;
        _logDirectory = logDirectory;
    }

    /// <summary>
    /// Gets the FFmpeg process.
    /// </summary>
    public Process? FfmpegProcess => _ffmpegProcess;

    /// <summary>
    /// Disposes the pipeline and cleans up resources.
    /// </summary>
    public void Dispose()
    {
        StopProcess();
        CleanupTempFiles();
    }

    private void StopProcess()
    {
        if (_ffmpegProcess is not null && !_ffmpegProcess.HasExited)
        {
            _ffmpegProcess.Kill();
            _ffmpegProcess.WaitForExit();
            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
        }
    }

    private readonly List<string> _tempFiles = new();

    private void CleanupTempFiles()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        _tempFiles.Clear();
    }

    /// <summary>
    /// Starts the VS Filter pipeline.
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
        // Generate VapourSynth script
        var scriptPath = GenerateVsScript(
            encodingOptions,
            width,
            height,
            pixelFormat,
            inputFramerate);

        // Build the complete FFmpeg command
        var args = BuildFfmpegArgs(
            sourcePath,
            outputPath,
            encodingOptions,
            videoStreamIndex,
            audioStreamIndex,
            scriptPath,
            videoCodec,
            audioCodec,
            videoBitrate,
            audioBitrate,
            segmentLength,
            segmentContainer,
            hardwareAccelerationType,
            hwDevice);

        // Create log file
        var logPrefix = "FFmpeg.VSFilter-";
        var logFilePath = Path.Combine(
            _logDirectory,
            $"{logPrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString()[..8]}.log");

        var logContent = $"=== FFmpeg VS Filter Pipeline ===\n\n";
        logContent += $"Source: {sourcePath}\n";
        logContent += $"Output: {outputPath}\n";
        logContent += $"Target: {encodingOptions.VsTargetWidth}x{encodingOptions.VsTargetHeight} @ {encodingOptions.VsTargetFpsNum}/{encodingOptions.VsTargetFpsDen}\n";
        logContent += $"Preset: {encodingOptions.VsFilterPreset}\n";
        logContent += $"Upscale model: {encodingOptions.VsUpscaleModel}\n";
        logContent += $"Interpolation model: {encodingOptions.VsInterpolationModel}\n\n";
        logContent += $"[FFmpeg] {encoderPath} {args}\n";

        File.WriteAllText(logFilePath, logContent);

        // Start FFmpeg
        _ffmpegProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = encoderPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _ffmpegProcess.Start();
    }

    private string GenerateVsScript(
        EncodingOptions encodingOptions,
        int sourceWidth,
        int sourceHeight,
        string sourcePixelFormat,
        double? inputFramerate)
    {
        // Use custom script if provided
        if (!string.IsNullOrWhiteSpace(encodingOptions.VsFilterCustomScript))
        {
            var customScriptPath = Path.Combine(_transcodeTempPath, $"vs-custom-{Guid.NewGuid()}.vpy");
            File.WriteAllText(customScriptPath, encodingOptions.VsFilterCustomScript);
            _tempFiles.Add(customScriptPath);
            return customScriptPath;
        }

        var script = new StringBuilder();

        script.AppendLine("import vapoursynth as vs");
        script.AppendLine("from vapoursynth import core");
        script.AppendLine();

        // Load TensorRT plugin if needed (for NVIDIA GPU acceleration)
        if (encodingOptions.VsUpscaleModel.Contains("trt", StringComparison.OrdinalIgnoreCase) ||
            encodingOptions.VsInterpolationModel.Contains("trt", StringComparison.OrdinalIgnoreCase))
        {
            script.AppendLine("# Load TensorRT plugin for GPU acceleration");
            script.AppendLine("try:");
            script.AppendLine("    core.std.LoadPlugin('/usr/lib/x86_64-linux-gnu/vapoursynth/libvstrt.so')");
            script.AppendLine("    core.std.LoadPlugin('/usr/local/lib/vapoursynth/libvstrt.so')");
            script.AppendLine("except:");
            script.AppendLine("    pass  # TensorRT plugin not found, will use CPU");
            script.AppendLine();
        }

        script.AppendLine("# Get input from FFmpeg");
        script.AppendLine("try:");
        script.AppendLine("    # Try vsrawsource first (named pipe input)");
        script.AppendLine("    import vsrawsource");
        script.AppendLine("    clip = core.vsraws.RawSource(clip, width=" + sourceWidth + ", height=" + sourceHeight + ", fmt=\"" + sourcePixelFormat + "\")");
        script.AppendLine("except:");
        script.AppendLine("    # Fallback: clip is already provided by FFmpeg via vsrawsource");
        script.AppendLine("    pass");
        script.AppendLine();

        // Apply upscaling based on preset/model
        var preset = encodingOptions.VsFilterPreset ?? "anime-upscaled";

        if (preset.Contains("upscale", StringComparison.OrdinalIgnoreCase) ||
            encodingOptions.VsUpscaleModel.Contains("realesr", StringComparison.OrdinalIgnoreCase))
        {
            script.AppendLine("# Upscaling with Real-ESRGAN");
            script.AppendLine("try:");
            script.AppendLine("    from vsrealesrgan import realesrgan");
            script.AppendLine("    clip = clip.resize.Bicubic(format=vs.RGBS, matrix_in_s='709')");
            script.AppendLine("    clip = realesrgan(clip, model=\"" + encodingOptions.VsUpscaleModel + "\", scale=2)");
            script.AppendLine("    clip = clip.resize.Bicubic(clip, width=" + encodingOptions.VsTargetWidth + ", height=" + encodingOptions.VsTargetHeight + ", format=vs.RGBS, matrix_s='709')");
            script.AppendLine("except Exception as e:");
            script.AppendLine("    print(f\"Real-ESRGAN failed: {e}, using bicubic fallback\")");
            script.AppendLine("    clip = clip.resize.Bicubic(width=" + encodingOptions.VsTargetWidth + ", height=" + encodingOptions.VsTargetHeight + ")");
            script.AppendLine();
        }
        else if (encodingOptions.VsUpscaleModel.Contains("swinput", StringComparison.OrdinalIgnoreCase))
        {
            script.AppendLine("# Upscaling with SWINIR");
            script.AppendLine("try:");
            script.AppendLine("    from vsinkswinir import SwinIR");
            script.AppendLine("    clip = clip.resize.Bicubic(format=vs.RGBS, matrix_in_s='709')");
            script.AppendLine("    clip = SwinIR(clip, model_path=\"/models/SwinIR\", scale=2)");
            script.AppendLine("    clip = clip.resize.Bicubic(clip, width=" + encodingOptions.VsTargetWidth + ", height=" + encodingOptions.VsTargetHeight + ", format=vs.RGBS, matrix_s='709')");
            script.AppendLine("except Exception as e:");
            script.AppendLine("    print(f\"SwinIR failed: {e}, using bicubic fallback\")");
            script.AppendLine("    clip = clip.resize.Bicubic(width=" + encodingOptions.VsTargetWidth + ", height=" + encodingOptions.VsTargetHeight + ")");
            script.AppendLine();
        }
        else
        {
            // Default: bicubic scaling
            script.AppendLine("# Simple bicubic upscaling (no AI upscaling)");
            script.AppendLine($"clip = clip.resize.Bicubic(width={encodingOptions.VsTargetWidth}, height={encodingOptions.VsTargetHeight})");
            script.AppendLine();
        }

        // Apply frame interpolation based on preset
        if (preset.Contains("interpolat", StringComparison.OrdinalIgnoreCase) ||
            encodingOptions.VsInterpolationModel.Contains("rife", StringComparison.OrdinalIgnoreCase))
        {
            var fpsNum = encodingOptions.VsTargetFpsNum;
            var fpsDen = encodingOptions.VsTargetFpsDen;

            script.AppendLine($"# Frame interpolation with RIFE ({fpsNum}/{fpsDen} fps)");
            script.AppendLine("try:");
            script.AppendLine("    from vsrife import rife");
            script.AppendLine("    # Check if TensorRT is available");
            script.AppendLine("    try:");
            script.AppendLine("        clip = rife(clip, model=\"" + encodingOptions.VsInterpolationModel + "\", factor_num=" + fpsNum + ", factor_den=" + fpsDen + ", trt=True, sc=True)");
            script.AppendLine("    except:");
            script.AppendLine("        # Fallback to CPU RIFE");
            script.AppendLine("        clip = rife(clip, model=\"" + encodingOptions.VsInterpolationModel + "\", factor_num=" + fpsNum + ", factor_den=" + fpsDen + ", trt=False, sc=True)");
            script.AppendLine("except Exception as e:");
            script.AppendLine($"    print(f\"RIFE interpolation failed: {{e}}, keeping original framerate\")");
            script.AppendLine();
        }

        // Convert to output format
        var outPixelFormat = encodingOptions.VsPixelFormat ?? "YUV420P8";
        script.AppendLine("# Convert to output format");
        script.AppendLine($"clip = clip.resize.Bicubic(format=vs.{outPixelFormat}, matrix_s='709')");
        script.AppendLine();
        script.AppendLine("# Set output");
        script.AppendLine("clip.set_output()");

        // Write script to temp file
        var scriptPath = Path.Combine(_transcodeTempPath, $"vsfilter-script-{Guid.NewGuid()}.vpy");
        File.WriteAllText(scriptPath, script.ToString());
        _tempFiles.Add(scriptPath);

        return scriptPath;
    }

    private string BuildFfmpegArgs(
        string sourcePath,
        string outputPath,
        EncodingOptions encodingOptions,
        int videoStreamIndex,
        int audioStreamIndex,
        string scriptPath,
        string videoCodec,
        string audioCodec,
        int? videoBitrate,
        int? audioBitrate,
        int segmentLength,
        string segmentContainer,
        string? hardwareAccelerationType,
        string? hwDevice)
    {
        var args = new StringBuilder();

        // Hardware acceleration
        if (!string.IsNullOrEmpty(hardwareAccelerationType) && hardwareAccelerationType != "none")
        {
            args.Append($"-hwaccel {hardwareAccelerationType}");

            if (!string.IsNullOrEmpty(hwDevice))
            {
                args.Append($" -hwaccel_device {hwDevice}");
            }

            args.Append(' ');
        }

        // Input
        args.Append($"-i \"{sourcePath}\"");
        args.Append($" -map 0:v:{videoStreamIndex}");
        args.Append($" -map 0:a:{audioStreamIndex}");
        args.Append(' ');

        // VapourSynth filter - the key feature!
        args.Append($"-vf \"vapoursynth=file={scriptPath}");
        if (encodingOptions.VsThreads > 0)
        {
            args.Append($":threads={encodingOptions.VsThreads}");
        }
        args.Append('"');
        args.Append(' ');

        // Video codec
        args.Append($"-c:v {videoCodec}");

        // Custom encoder arguments or defaults
        if (!string.IsNullOrWhiteSpace(encodingOptions.VsEncoderArgs))
        {
            args.Append($" {encodingOptions.VsEncoderArgs}");
        }
        else
        {
            // Default encoding parameters for high quality
            if (videoBitrate.HasValue)
            {
                args.Append($" -b:v {videoBitrate.Value}k");
                args.Append($" -maxrate {videoBitrate.Value}k");
                args.Append($" -bufsize {videoBitrate.Value * 2}k");
            }
            else
            {
                // High quality defaults for VS-filtered content
                args.Append(" -preset slow");
                args.Append(" -crf 18");
            }
        }
        args.Append(' ');

        // Audio codec
        args.Append($"-c:a {audioCodec}");
        if (audioBitrate.HasValue)
        {
            args.Append($" -b:a {audioBitrate.Value}k");
        }
        args.Append(' ');

        // HLS output
        args.Append("-f hls");
        args.Append($" -hls_time {segmentLength}");
        args.Append($" -hls_segment_type {(segmentContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? "fmp4" : "mpegts")}");

        var ext = segmentContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? ".m4s" : ".ts";
        var initName = segmentContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(outputPath) + "-init.m4s"
            : string.Empty;

        if (!string.IsNullOrEmpty(initName))
        {
            args.Append($" -hls_fmp4_init_filename \"{initName}\"");
        }

        var segName = Path.GetFileNameWithoutExtension(outputPath);
        args.Append($" -hls_segment_filename \"{Path.GetDirectoryName(outputPath)}/{segName}%d{ext}\"");
        args.Append(" -hls_list_size 0");
        args.Append($" -max_muxing_queue_size {encodingOptions.MaxMuxingQueueSize}");
        args.Append(" -copyts -avoid_negative_ts disabled");
        args.Append($" -y \"{outputPath}\"");

        return args.ToString();
    }
}
