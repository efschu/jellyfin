using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Controller for VapourSynth Filter pipeline configuration.
/// Uses the VapourSynth demuxer (-f vapoursynth) approach with dynamically
/// generated .vpy scripts.
/// </summary>
[Route("EncodingOptions/VsFilter")]
[Authorize]
[ApiController]
public class VsFilterController : ControllerBase
{
    private readonly IConfigurationManager _configurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="VsFilterController"/> class.
    /// </summary>
    /// <param name="configurationManager">Instance of the <see cref="IConfigurationManager"/> interface.</param>
    public VsFilterController(IConfigurationManager configurationManager)
    {
        _configurationManager = configurationManager;
    }

    /// <summary>
    /// Gets the VS Filter pipeline configuration.
    /// </summary>
    /// <returns>The VS Filter options.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(VsFilterOptions), StatusCodes.Status200OK)]
    public ActionResult<VsFilterOptions> GetVsFilterOptions()
    {
        var encodingOptions = _configurationManager.GetEncodingOptions();

        return new VsFilterOptions
        {
            EnableVsFilterPipeline = encodingOptions.EnableVsFilterPipeline,
            VsFilterPreset = encodingOptions.VsFilterPreset,
            VsFilterCustomScript = encodingOptions.VsFilterCustomScript,
            VsUpscaleModel = encodingOptions.VsUpscaleModel,
            VsInterpolationModel = encodingOptions.VsInterpolationModel,
            VsTargetWidth = encodingOptions.VsTargetWidth,
            VsTargetHeight = encodingOptions.VsTargetHeight,
            VsTargetFpsNum = encodingOptions.VsTargetFpsNum,
            VsTargetFpsDen = encodingOptions.VsTargetFpsDen,
            VsPixelFormat = encodingOptions.VsPixelFormat,
            VsThreads = encodingOptions.VsThreads,
            VsEncoderArgs = encodingOptions.VsEncoderArgs
        };
    }

    /// <summary>
    /// Updates the VS Filter pipeline configuration.
    /// </summary>
    /// <param name="options">The VS Filter options.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> UpdateVsFilterOptions([FromBody] VsFilterOptions options)
    {
        var encodingOptions = _configurationManager.GetEncodingOptions();

        encodingOptions.EnableVsFilterPipeline = options.EnableVsFilterPipeline;
        encodingOptions.VsFilterPreset = options.VsFilterPreset;
        encodingOptions.VsFilterCustomScript = options.VsFilterCustomScript;
        encodingOptions.VsUpscaleModel = options.VsUpscaleModel;
        encodingOptions.VsInterpolationModel = options.VsInterpolationModel;
        encodingOptions.VsTargetWidth = options.VsTargetWidth;
        encodingOptions.VsTargetHeight = options.VsTargetHeight;
        encodingOptions.VsTargetFpsNum = options.VsTargetFpsNum;
        encodingOptions.VsTargetFpsDen = options.VsTargetFpsDen;
        encodingOptions.VsPixelFormat = options.VsPixelFormat;
        encodingOptions.VsThreads = options.VsThreads;
        encodingOptions.VsEncoderArgs = options.VsEncoderArgs;

        await _configurationManager.SaveEncodingOptionsAsync(encodingOptions);

        return NoContent();
    }
}

/// <summary>
/// VS Filter pipeline configuration options.
/// </summary>
public class VsFilterOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the VS Filter pipeline is enabled.
    /// </summary>
    public bool EnableVsFilterPipeline { get; set; }

    /// <summary>
    /// Gets or sets the preset name.
    /// Valid values: "anime-upscaled", "anime-interpolated", "upscale-only", "custom"
    /// </summary>
    public string VsFilterPreset { get; set; } = "anime-upscaled";

    /// <summary>
    /// Gets or sets the path to a custom VapourSynth script.
    /// When set, this script is used instead of the generated one.
    /// </summary>
    public string VsFilterCustomScript { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the upscaling model.
    /// </summary>
    public string VsUpscaleModel { get; set; } = "realesr-animevideov3";

    /// <summary>
    /// Gets or sets the frame interpolation model.
    /// </summary>
    public string VsInterpolationModel { get; set; } = "rife-4.25";

    /// <summary>
    /// Gets or sets the target output width.
    /// </summary>
    public int VsTargetWidth { get; set; } = 3840;

    /// <summary>
    /// Gets or sets the target output height.
    /// </summary>
    public int VsTargetHeight { get; set; } = 2160;

    /// <summary>
    /// Gets or sets the FPS numerator.
    /// </summary>
    public int VsTargetFpsNum { get; set; } = 60000;

    /// <summary>
    /// Gets or sets the FPS denominator.
    /// </summary>
    public int VsTargetFpsDen { get; set; } = 1001;

    /// <summary>
    /// Gets or sets the pixel format.
    /// </summary>
    public string VsPixelFormat { get; set; } = "YUV420P10";

    /// <summary>
    /// Gets or sets the VapourSynth thread count (0 = auto).
    /// </summary>
    public int VsThreads { get; set; }

    /// <summary>
    /// Gets or sets custom FFmpeg encoder arguments.
    /// </summary>
    public string VsEncoderArgs { get; set; } = string.Empty;
}
