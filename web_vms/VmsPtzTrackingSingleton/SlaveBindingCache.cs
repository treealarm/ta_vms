using System.Collections.Concurrent;
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
  /// <summary>Whether this binding can actually aim the camera by the method it was given.</summary>
  /// <returns>Null when it can, otherwise what is missing.</returns>
  public string? Unusable()
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
/// routes and calibration (CLAUDE.md); the TTL stays as the backstop for a VmsCfg that cannot be
/// read.
/// </remarks>
public sealed class SlaveBindingCache(
  CfgService.CfgServiceClient cfg,
  ILogger<SlaveBindingCache> logger)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

  private sealed record Entry(SlaveBindingModel? Model, DateTime FetchedAt);

  private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

  public void Invalidate(IReadOnlyCollection<string> cameraIds)
  {
    if (cameraIds.Count == 0)
    {
      _entries.Clear();
      return;
    }

    foreach (var cameraId in cameraIds)
      _entries.TryRemove(cameraId, out _);
  }

  public async Task<SlaveBindingModel?> GetAsync(string slaveCameraId, CancellationToken ct)
  {
    if (_entries.TryGetValue(slaveCameraId, out var cached) && DateTime.UtcNow - cached.FetchedAt < Ttl)
      return cached.Model;

    try
    {
      var response = await cfg.GetPtzSlaveBindingAsync(
        new GetPtzSlaveBindingRequest { SlaveCameraId = slaveCameraId }, cancellationToken: ct);

      var model = response.Found ? Build(response.Binding) : null;
      _entries[slaveCameraId] = new Entry(model, DateTime.UtcNow);
      return model;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Could not load the slave binding for camera '{Camera}'", slaveCameraId);
      // A failed fetch is not an answer: the previous binding keeps aiming the camera through a
      // VmsCfg hiccup rather than the slave going blind.
      return cached?.Model;
    }
  }

  /// <summary>Re-reads one binding, ignoring what is held.</summary>
  /// <remarks>
  /// For the survey wizard, whose next action after saving a point is to test it. The invalidation
  /// event is on its way over the bus at that moment and may not have arrived, and testing against
  /// the survey as it was a moment ago would send the camera somewhere the operator did not just
  /// ask for.
  /// </remarks>
  public Task<SlaveBindingModel?> ReloadAsync(string slaveCameraId, CancellationToken ct)
  {
    _entries.TryRemove(slaveCameraId, out _);
    return GetAsync(slaveCameraId, ct);
  }

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
