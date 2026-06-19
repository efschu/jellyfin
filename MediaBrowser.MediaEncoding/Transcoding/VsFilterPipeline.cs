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
/// This replaces standard transcoding with AI-powered upscaling and frame interpolation.
/// </summary>
public sealed class VsFilterPipeline : IDisposable
{
    private readonly string _transcodeTempPath;
    private readonly string _logDirectory;
    private Process? _ffmpegProcess;
    private readonly List<string> _tempFiles = new();

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

    /// <inheritdoc />
    public void Dispose()
    {
        StopProcess();
        CleanupTempFiles();
    }

    private void StopProcess()
    {
        if (_ffmpegProcess is not null && !_ffmpegProcess.HasExited)
        {
            try
            {
                _ffmpegProcess.Kill();
                _ffmpegProcess.WaitForExit();
            }
            catch
            {
                // Ignore errors during cleanup
            }
            finally
            {
                _ffmpegProcess.Dispose();
                _ffmpegProcess = null;
            }
        }
    }

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
    /// <param name="commandLineArguments">Base FFmpeg command line arguments.</param>
    /// <param name="videoStreamIndex">Video stream index.</param>
    /// <param name="audioStreamIndex">Audio stream index.</param>
    /// <param name="videoCodec">Output video codec.</param>
    /// <param name="audioCodec">Output audio codec.</param>
    /// <param name="segmentLength">Segment length in seconds.</param>
    /// <param name="segmentContainer">Segment container format.</param>
    /// <param name="videoBitrate">Video bitrate.</param>
    /// <param name="audioBitrate">Audio bitrate.</param>
    /// <param name="hardwareAccelerationType">Hardware acceleration type.</param>
    /// <param name="hwDevice">Hardware device path.</param>
    public void Start(
        string sourcePath,
        string outputPath,
        string encoderPath,
        EncodingOptions encodingOptions,
        string commandLineArguments,
        int videoStreamIndex,
        int audioStreamIndex,
        string videoCodec,
        string audioCodec,
        int segmentLength,
        string segmentContainer,
        int? videoBitrate,
        int? audioBitrate,
        string? hardwareAccelerationType,
        string? hwDevice)
    {
        // Generate VapourSynth script based on preset
        var scriptPath = GenerateVsScript(encodingOptions);

        // Build the FFmpeg command with VS filter
        var args = BuildFfmpegArgs(
            sourcePath,
            outputPath,
            encodingOptions,
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
            $"{logPrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss_{Guid.NewGuid():N}.log");

        var logContent = new StringBuilder();
        logContent.AppendLine("=== FFmpeg VapourSynth Filter Pipeline ===");
        logContent.AppendLine($"Source: {sourcePath}");
        logContent.AppendLine($"Output: {outputPath}");
        logContent.AppendLine($"Target: {encodingOptions.VsTargetWidth}x{encodingOptions.VsTargetHeight} @ {encodingOptions.VsTargetFpsNum}/{encodingOptions.VsTargetFpsDen}");
        logContent.AppendLine($"Preset: {encodingOptions.VsFilterPreset}");
        logContent.AppendLine($"Upscale model: {encodingOptions.VsUpscaleModel}");
        logContent.AppendLine($"Interpolation model: {encodingOptions.VsInterpolationModel}");
        logContent.AppendLine();
        logContent.AppendLine($"[FFmpeg] {encoderPath} {args}");
        logContent.AppendLine();
        logContent.AppendLine("=== VapourSynth Script ===");
        logContent.AppendLine(File.ReadAllText(scriptPath));

        File.WriteAllText(logFilePath, logContent.ToString());

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

    private string GenerateVsScript(EncodingOptions encodingOptions)
    {
        var script = new StringBuilder();

        // Use custom script if provided
        if (!string.IsNullOrWhiteSpace(encodingOptions.VsFilterCustomScript))
        {
            var customPath = Path.Combine(_transcodeTempPath, $"vs-custom-{Guid.NewGuid():N}.vpy");
            File.WriteAllText(customPath, encodingOptions.VsFilterCustomScript);
            _tempFiles.Add(customPath);
            return customPath;
        }

        script.AppendLine("import vapoursynth as vs");
        script.AppendLine("from vapoursynth import core");
        script.AppendLine();

        // Load TensorRT plugin if needed
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

        var preset = encodingOptions.VsFilterPreset ?? "anime-upscaled";

        // Upscaling based on preset/model
        if (preset.Contains("upscale", StringComparison.OrdinalIgnoreCase) ||
            encodingOptions.VsUpscaleModel.Contains("realesr", StringComparison.OrdinalIgnoreCase))
        {
            script.AppendLine("# Upscaling with Real-ESRGAN");
            script.AppendLine("try:");
            script.AppendLine("    from vsrealesrgan import realesrgan");
            script.AppendLine("    clip = clip.resize.Bicubic(format=vs.RGBS, matrix_in_s='709')");
            script.AppendLine($"    clip = realesrgan(clip, model=\"{encodingOptions.VsUpscaleModel}\", scale=2)");
            script.AppendLine($"    clip = clip.resize.Bicubic(clip, width={encodingOptions.VsTargetWidth}, height={encodingOptions.VsTargetHeight}, format=vs.RGBS, matrix_s='709')");
            script.AppendLine("except Exception as e:");
            script.AppendLine("    print(f\"Real-ESRGAN failed: {e}, using bicubic fallback\")");
            script.AppendLine($"    clip = clip.resize.Bicubic(width={encodingOptions.VsTargetWidth}, height={encodingOptions.VsTargetHeight})");
            script.AppendLine();
        }
        else
        {
            // Default: bicubic scaling
            script.AppendLine($"# Simple bicubic upscaling (no AI upscaling)");
            script.AppendLine($"clip = clip.resize.Bicubic(width={encodingOptions.VsTargetWidth}, height={encodingOptions.VsTargetHeight})");
            script.AppendLine();
        }

        // Frame interpolation based on preset
        if (preset.Contains("interpolat", StringComparison.OrdinalIgnoreCase) ||
            encodingOptions.VsInterpolationModel.Contains("rife", StringComparison.OrdinalIgnoreCase))
        {
            var fpsNum = encodingOptions.VsTargetFpsNum;
            var fpsDen = encodingOptions.VsTargetFpsDen;

            script.AppendLine($"# Frame interpolation with RIFE ({fpsNum}/{fpsDen} fps)");
            script.AppendLine("try:");
            script.AppendLine("    from vsrife import rife");
            script.AppendLine("    try:");
            script.AppendLine($"        clip = rife(clip, model=\"{encodingOptions.VsInterpolationModel}\", factor_num={fpsNum}, factor_den={fpsDen}, trt=True, sc=True)");
            script.AppendLine("    except:");
            script.AppendLine($"        clip = rife(clip, model=\"{encodingOptions.VsInterpolationModel}\", factor_num={fpsNum}, factor_den={fpsDen}, trt=False, sc=True)");
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
        var scriptPath = Path.Combine(_transcodeTempPath, $"vsfilter-script-{Guid.NewGuid():N}.vpy");
        File.WriteAllText(scriptPath, script.ToString());
        _tempFiles.Add(scriptPath);

        return scriptPath;
    }

    private string BuildFfmpegArgs(
        string sourcePath,
        string outputPath,
        EncodingOptions encodingOptions,
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
        args.Append($"-i \"{sourcePath}\" ");

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
            // High quality defaults
            if (videoBitrate.HasValue)
            {
                args.Append($" -b:v {videoBitrate.Value}k");
                args.Append($" -maxrate {videoBitrate.Value}k");
                args.Append($" -bufsize {videoBitrate.Value * 2}k");
            }
            else
            {
                args.Append(" -preset slow -crf 18");
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

        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var segName = Path.GetFileNameWithoutExtension(outputPath);
        var ext = segmentContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? ".m4s" : ".ts";

        args.Append($" -hls_segment_filename \"{directory}/{segName}%d{ext}\"");
        args.Append(" -hls_list_size 0");
        args.Append($" -max_muxing_queue_size {encodingOptions.MaxMuxingQueueSize}");
        args.Append(" -copyts -avoid_negative_ts disabled");
        args.Append($" -y \"{outputPath}\"");

        return args.ToString();
    }
}
