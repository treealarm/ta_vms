using Vms.Cfg;

namespace VmsPtzTrackingSingleton;

/// <summary>How a slave is aimed at its master's objects, ready to use.</summary>
/// <param name="Fit">
/// Null when the survey cannot carry an interpolation -- too few points with a readable position,
/// or all of them on one line. The presets method does not need it, so this being null is only
/// fatal to <see cref="PtzSlaveBindingMethod.PtzSlaveBindingAbsolute"/>.
/// </param>
/// <param name="FitError">Why there is no fit, for the operator.</param>
public sealed record SlaveBindingModel(
  string SlaveCameraId,
  string MasterCameraId,
  PtzSlaveBindingMethod Method,
  IReadOnlyList<BindingPoint> Points,
  BindingFit? Fit,
  string? FitError)
{
  /// <summary>Whether the survey can aim the camera by the method it was given.</summary>
  /// <remarks>
  /// Only for the methods that aim by the survey; whether a slave can aim at all, whatever its
  /// method, is <see cref="SlaveAim.Unusable"/>.
  /// </remarks>
  /// <returns>Null when it can, otherwise what is missing.</returns>
  public string? SurveyUnusable()
  {
    if (Points.Count == 0)
      return "The binding has no marked points";

    if (Method == PtzSlaveBindingMethod.PtzSlaveBindingPresets)
    {
      // Every point, not most of them: the nearest point to wherever the object appears is the one
      // used, so a point without a preset is a patch of the master's frame with nothing to aim at.
      var without = Points.Count(p => string.IsNullOrEmpty(p.PresetToken));
      return without == 0
        ? null
        : $"{without} of {Points.Count} marked points have no preset";
    }

    return Fit is null ? FitError ?? "The marked points do not carry a mapping" : null;
  }
}

/// <summary>
/// Cached master/slave bindings, fitted once per change rather than per detection.
/// </summary>
/// <remarks>
/// Fitting is a linear solve of order N+3 plus one more per point for the leave-one-out check, and
/// the detections arrive several times a second -- so the fit belongs here, next to the fetch, and
/// not on the aiming path. Invalidated through the same <c>server-config-changed</c> signal as
/// routes and calibration (CLAUDE.md); see <see cref="TtlCache{T}"/> for the rest.
/// </remarks>
public sealed class SlaveBindingCache(
  CfgService.CfgServiceClient cfg,
  ILogger<SlaveBindingCache> logger)
{
  private readonly TtlCache<SlaveBindingModel> _cache = new(
    async (slaveCameraId, ct) =>
    {
      var response = await cfg.GetPtzSlaveBindingAsync(
        new GetPtzSlaveBindingRequest { SlaveCameraId = slaveCameraId }, cancellationToken: ct);
      return response.Found ? Build(response.Binding) : null;
    },
    TimeSpan.FromMinutes(1), logger, "slave binding");

  public void Invalidate(IReadOnlyCollection<string> cameraIds) => _cache.Invalidate(cameraIds);

  public Task<SlaveBindingModel?> GetAsync(string slaveCameraId, CancellationToken ct) =>
    _cache.GetAsync(slaveCameraId, ct);

  /// <summary>Re-reads one binding, ignoring what is held.</summary>
  /// <remarks>
  /// For the survey wizard, whose next action after saving a point is to test it. The invalidation
  /// event is on its way over the bus at that moment and may not have arrived, and testing against
  /// the survey as it was a moment ago would send the camera somewhere the operator did not just
  /// ask for.
  /// </remarks>
  public Task<SlaveBindingModel?> ReloadAsync(string slaveCameraId, CancellationToken ct) =>
    _cache.ReloadAsync(slaveCameraId, ct);

  /// <summary>Fits a binding without consulting the cache — for the wizard, which has just written it.</summary>
  public static SlaveBindingModel Build(PtzSlaveBinding binding)
  {
    var points = binding.Points
      .Select(p => new BindingPoint(
        p.MasterX,
        p.MasterY,
        p.HasPan ? p.Pan : null,
        p.HasTilt ? p.Tilt : null,
        string.IsNullOrEmpty(p.PresetToken) ? null : p.PresetToken,
        string.IsNullOrEmpty(p.PresetName) ? null : p.PresetName))
      .ToList();

    // Fitted whatever the method says. The survey is one thing serving both, so a binding on the
    // presets method still reports what its interpolation would look like — which is what lets the
    // operator switch methods and see straight away whether the points hold up.
    PtzSlaveBindingMath.TryFit(points, PtzSlaveBindingMath.DefaultLambda, out var fit, out var error);

    return new SlaveBindingModel(
      binding.SlaveCameraId, binding.MasterCameraId, binding.Method, points, fit, error);
  }
}
