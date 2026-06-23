using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using MediaBrowser.Model.Configuration;

namespace MediaBrowser.MediaEncoding.Transcoding;

/// <summary>
/// Pipeline that uses FFmpeg's VapourSynth DEMUXER (-f vapoursynth -i script.vpy).
/// This is the working approach for efschu/FFmpeg fork - the VS filter (-vf vapoursynth=)
/// is not yet available, but the demuxer is fully functional.
/// 
/// The VapourSynth script (.vpy) is generated dynamically and contains the
/// complete processing chain (upscale + interpolation + output).
/// </summary>
public sealed class VsFilterPipeline : IDisposable
{
    private readonly string _transcodeTempPath;
    private readonly string _logDirectory;
    private readonly List<string> _tempFiles = new List<string>();
    private Process? _ffmpegProcess;
    private int _state;
    private string? _scriptPath;

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
        if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
        {
            try
            {
                _ffmpegProcess.Kill();
                _ffmpegProcess.WaitForExit();
            }
            catch
            {
                // Ignore
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
                // Ignore
            }
        }
        _tempFiles.Clear();
    }

    /// <summary>
    /// Starts the VapourSynth demuxer pipeline.
    /// </summary>
    /// <param name="sourcePath">Source media path.</param>
    /// <param name="outputPath">Output HLS playlist path.</param>
    /// <param name="encoderPath">FFmpeg encoder path.</param>
    /// <param name="encodingOptions">Encoding options.</param>
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
        // Generate VapourSynth script - it must read the source file and apply processing
        _scriptPath = GenerateVsScript(sourcePath, encodingOptions, videoStreamIndex);

        // Build FFmpeg command using the VapourSynth demuxer
        var args = BuildFfmpegArgs(
            _scriptPath,
            outputPath,
            sourcePath,
            encodingOptions,
            audioStreamIndex,
            videoCodec,
            audioCodec,
            videoBitrate,
            audioBitrate,
            segmentLength,
            segmentContainer,
            hardwareAccelerationType,
            hwDevice);

        // Create log file
        var logPrefix = "FFmpeg.VSDemuxer-";
        var logFilePath = Path.Combine(
            _logDirectory,
            $"{logPrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}.log");

        var logContent = new StringBuilder();
        logContent.AppendLine("=== FFmpeg VapourSynth Demuxer Pipeline ===");
        logContent.AppendLine($"Source: {sourcePath}");
        logContent.AppendLine($"Output: {outputPath}");
        logContent.AppendLine($"VS Script: {_scriptPath}");
        logContent.AppendLine($"Target: {encodingOptions.VsTargetWidth}x{encodingOptions.VsTargetHeight} @ {encodingOptions.VsTargetFpsNum}/{encodingOptions.VsTargetFpsDen}");
        logContent.AppendLine($"Preset: {encodingOptions.VsFilterPreset}");
        logContent.AppendLine($"Upscale model: {encodingOptions.VsUpscaleModel}");
        logContent.AppendLine($"Interpolation model: {encodingOptions.VsInterpolationModel}");
        logContent.AppendLine();
        logContent.AppendLine($"[FFmpeg] {encoderPath} {args}");
        logContent.AppendLine();
        logContent.AppendLine("=== VapourSynth Script ===");
        logContent.AppendLine(File.ReadAllText(_scriptPath));

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

    /// <summary>
    /// Generates a VapourSynth script that loads the source, applies AI processing, and outputs.
    /// </summary>
    private string GenerateVsScript(string sourcePath, EncodingOptions options, int videoStreamIndex)
    {
        var preset = options.VsFilterPreset ?? "anime-upscaled";

        // Check for user-provided custom script
        if (!string.IsNullOrWhiteSpace(options.VsFilterCustomScript) &&
            File.Exists(options.VsFilterCustomScript))
        {
            // User provided a custom script - use it directly
            // The script must handle the source itself
            return options.VsFilterCustomScript;
        }

        // Use the system-installed VapourSynth Python module
        // We use a special import path that works with both system Python and the bundled one
        var script = new StringBuilder();

        script.AppendLine("# VapourSynth script generated by Jellyfin VS Filter Pipeline");
        script.AppendLine($"# Generated at: {DateTime.UtcNow:O}");
        script.AppendLine($"# Source: {sourcePath}");
        script.AppendLine($"# Stream index: {videoStreamIndex}");
        script.AppendLine();

        script.AppendLine("import vapoursynth as vs");
        script.AppendLine("from vapoursynth import core");
        script.AppendLine("import sys");
        script.AppendLine("import os");
        script.AppendLine();

        // Try to load TensorRT plugin if needed
        if (options.VsUpscaleModel.Contains("trt", StringComparison.OrdinalIgnoreCase) ||
            options.VsInterpolationModel.Contains("trt", StringComparison.OrdinalIgnoreCase))
        {
            script.AppendLine("# Load TensorRT plugin");
            script.AppendLine("try:");
            script.AppendLine("    core.std.LoadPlugin('/usr/lib/x86_64-linux-gnu/vapoursynth/libvstrt.so')");
            script.AppendLine("except:");
            script.AppendLine("    pass");
            script.AppendLine();
        }

        // Load input using lsmash or bestsource
        script.AppendLine("# Load source video");
        script.AppendLine("clip = None");
        script.AppendLine("try:");
        script.AppendLine("    from vapoursynth import core");
        script.AppendLine("    # Try bestsource first (fastest)");
        script.AppendLine("    try:");
        script.Append("        clip = core.bs.VideoSource(");
        script.Append($"source='{EscapePythonString(sourcePath)}', ");
        if (videoStreamIndex >= 0) script.Append($"trackindex={videoStreamIndex}");
        script.AppendLine(")");
        script.AppendLine("    except Exception as e1:");
        script.AppendLine("        print(f\"bestsource failed: {{e1}}, trying lsmash\")");
        script.AppendLine("        try:");
        script.Append("            clip = core.lsmas.LWLibavSource(");
        script.Append($"source='{EscapePythonString(sourcePath)}'");
        if (videoStreamIndex >= 0) script.Append($", stream_index={videoStreamIndex}");
        script.AppendLine(")");
        script.AppendLine("        except Exception as e2:");
        script.AppendLine("            print(f\"lsmash failed: {{e2}}, trying ffms2\")");
        script.AppendLine("            try:");
        script.Append("                clip = core.ffms2.Source(");
        script.Append($"source='{EscapePythonString(sourcePath)}'");
        if (videoStreamIndex >= 0) script.Append($", track={videoStreamIndex}");
        script.AppendLine(")");
        script.AppendLine("            except Exception as e3:");
        script.AppendLine("                raise RuntimeError(f\"All source loaders failed: {{e1}}, {{e2}}, {{e3}}\")");
        script.AppendLine("except Exception as e:");
        script.AppendLine($"    raise RuntimeError(f\"Source loading failed: {{e}}\")");
        script.AppendLine();

        // Apply AI upscaling based on preset
        if (preset.Contains("upscale", StringComparison.OrdinalIgnoreCase) ||
            options.VsUpscaleModel.Contains("realesr", StringComparison.OrdinalIgnoreCase))
        {
            script.AppendLine("# AI Upscaling with Real-ESRGAN");
            script.AppendLine("try:");
            script.AppendLine("    import realesrgan");
            script.AppendLine("    # Convert to RGB float for AI processing");
            script.AppendLine("    clip = core.resize.Bicubic(clip, format=vs.RGBS, matrix_in_s='709')");
            script.AppendLine($"    clip = realesrgan(clip, model=\"{options.VsUpscaleModel}\", scale=2)");
            script.AppendLine($"    clip = core.resize.Bicubic(clip, width={options.VsTargetWidth}, height={options.VsTargetHeight}, format=vs.RGBS, matrix_s='709')");
            script.AppendLine("except Exception as e:");
            script.AppendLine("    print(f\"Real-ESRGAN failed: {e}, using bicubic fallback\")");
            script.AppendLine($"    clip = core.resize.Bicubic(clip, width={options.VsTargetWidth}, height={options.VsTargetHeight})");
            script.AppendLine();
        }
        else
        {
            // Simple bicubic scaling
            script.AppendLine("# Bicubic upscaling");
            script.AppendLine($"clip = core.resize.Bicubic(clip, width={options.VsTargetWidth}, height={options.VsTargetHeight})");
            script.AppendLine();
        }

        // Apply frame interpolation based on preset
        if (preset.Contains("interpolat", StringComparison.OrdinalIgnoreCase) ||
            options.VsInterpolationModel.Contains("rife", StringComparison.OrdinalIgnoreCase))
        {
            var fpsNum = options.VsTargetFpsNum;
            var fpsDen = options.VsTargetFpsDen;

            script.AppendLine($"# Frame interpolation with RIFE ({fpsNum}/{fpsDen} fps)");
            script.AppendLine("try:");
            script.AppendLine("    from vsrife import RIFE");
            script.AppendLine("    # Try with TensorRT first (GPU acceleration)");
            script.AppendLine("    try:");
            script.AppendLine($"        clip = RIFE(clip, model=\"{options.VsInterpolationModel}\", factor_num={fpsNum}, factor_den={fpsDen}, trt=True, sc=True)");
            script.AppendLine("    except Exception as e:");
            script.AppendLine("        print(f\"RIFE with TensorRT failed: {e}, trying CPU mode\")");
            script.AppendLine($"        clip = RIFE(clip, model=\"{options.VsInterpolationModel}\", factor_num={fpsNum}, factor_den={fpsDen}, trt=False, sc=True)");
            script.AppendLine("except Exception as e:");
            script.AppendLine("    print(f\"RIFE failed: {e}, keeping original framerate\")");
            script.AppendLine();
        }

        // Convert to output format
        var outPixelFormat = options.VsPixelFormat ?? "YUV420P8";
        script.AppendLine("# Convert to output pixel format");
        script.AppendLine($"clip = core.resize.Bicubic(clip, format=vs.{outPixelFormat}, matrix_s='709')");
        script.AppendLine();

        // Set output
        script.AppendLine("# Set output");
        script.AppendLine("clip.set_output()");

        // Write script to temp file
        var scriptPath = Path.Combine(_transcodeTempPath, $"vsfilter-{Guid.NewGuid():N}.vpy");
        File.WriteAllText(scriptPath, script.ToString());
        _tempFiles.Add(scriptPath);

        return scriptPath;
    }

    /// <summary>
    /// Escapes a string for use in Python code.
    /// </summary>
    private static string EscapePythonString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("'", "\\'");
    }

    /// <summary>
    /// Builds the FFmpeg command-line arguments using the VapourSynth demuxer.
    /// </summary>
    private string BuildFfmpegArgs(
        string scriptPath,
        string outputPath,
        string sourcePath,
        EncodingOptions options,
        int audioStreamIndex,
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

        // Use VapourSynth demuxer as input
        // The script handles reading the source, so we just point at the .vpy file
        args.Append($"-f vapoursynth -i \"{scriptPath}\" ");

        // Audio from the source file (not the VS script)
        // FFmpeg can have multiple inputs - we need to map audio from the source
        args.Append($"-i \"{sourcePath}\" ");

        // Map: video from VS script (output 0:v), audio from source (input 1:a:index)
        args.Append("-map 0:v:0 ");
        args.Append($"-map 1:a:{audioStreamIndex} ");

        // Video codec
        args.Append($"-c:v {videoCodec} ");

        // Custom encoder args or defaults
        if (!string.IsNullOrWhiteSpace(options.VsEncoderArgs))
        {
            args.Append($"{options.VsEncoderArgs} ");
        }
        else if (videoBitrate.HasValue)
        {
            args.Append($"-b:v {videoBitrate.Value}k -maxrate {videoBitrate.Value}k -bufsize {videoBitrate.Value * 2}k ");
        }
        else
        {
            args.Append("-preset slow -crf 18 ");
        }

        // Audio codec
        args.Append($"-c:a {audioCodec} ");
        if (audioBitrate.HasValue)
        {
            args.Append($"-b:a {audioBitrate.Value}k ");
        }

        // HLS output
        args.Append("-f hls ");
        args.Append($"-hls_time {segmentLength} ");

        var isFmp4 = segmentContainer.Equals("mp4", StringComparison.OrdinalIgnoreCase);
        args.Append($"-hls_segment_type {(isFmp4 ? "fmp4" : "mpegts")} ");

        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var segName = Path.GetFileNameWithoutExtension(outputPath);
        var ext = isFmp4 ? ".m4s" : ".ts";
        var initName = isFmp4 ? $"{segName}-init.m4s" : string.Empty;

        if (!string.IsNullOrEmpty(initName))
        {
            args.Append($"-hls_fmp4_init_filename \"{initName}\" ");
        }

        args.Append($"-hls_segment_filename \"{directory}/{segName}%d{ext}\" ");
        args.Append("-hls_list_size 0 ");
        args.Append($"-max_muxing_queue_size {options.MaxMuxingQueueSize} ");
        args.Append("-y \"");
        args.Append(outputPath);
        args.Append('"');

        return args.ToString();
    }
}
