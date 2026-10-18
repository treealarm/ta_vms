using Grpc.Core;
using Vms.Analytics;
using Vms.Cfg;
using Vms.Onvif;
using Vms.PtzTracking;
using web_srv.Types.Dto;

namespace web_srv;

/// <summary>Runs PTZ test moves and turns observations into a per-camera calibration.</summary>
public sealed class PtzCalibrationService(
  CfgService.CfgServiceClient cfg,
  VmsAnalyticsService.VmsAnalyticsServiceClient analytics,
  PtzTrackingService.PtzTrackingServiceClient tracking,
  OnvifRouter onvif,
  ICameraManager cameras,
  PtzCalibrationWizardStore wizardStore,
  ILogger<PtzCalibrationService> logger)
{
  private const float TestSpeed = 0.25f;
  private static readonly TimeSpan MovePulse = TimeSpan.FromMilliseconds(350);
  private static readonly TimeSpan SettleAfterStop = TimeSpan.FromMilliseconds(500);
  private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(30);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: ct);

  public async Task<PtzCalibrationDto?> GetAsync(string cameraId, CancellationToken ct)
  {
    var response = await cfg.GetPtzCalibrationAsync(
      new GetPtzCalibrationRequest { CameraId = cameraId }, Options(ct));
    return response.Found ? ToDto(response.Calibration) : null;
  }

  public async Task<bool> RemoveAsync(string cameraId, CancellationToken ct)
  {
    var response = await cfg.RemovePtzCalibrationAsync(
      new RemovePtzCalibrationRequest { CameraId = cameraId }, Options(ct));
    return response.Success;
  }

  public async Task<PtzCalibrationRunResultDto> RunAutoAsync(string cameraId, CancellationToken ct)
  {
    var prep = await PrepareAsync(cameraId, ct);
    if (prep.Error is not null)
      return Fail(prep.Error);

    try
    {
      var beforePan = await SnapshotAsync(cameraId, ct);
      if (beforePan is null)
        return Fail("Could not capture a snapshot before the pan test");

      await PulseMoveAsync(cameraId, prep.ProfileToken, TestSpeed, 0f, ct);
      var afterPan = await SnapshotAsync(cameraId, ct);
      await PulseMoveAsync(cameraId, prep.ProfileToken, -TestSpeed, 0f, ct);
      if (afterPan is null)
        return Fail("Could not capture a snapshot after the pan test");

      var panMotion = await EstimateMotionAsync(beforePan, afterPan, ct);
      if (panMotion is null)
        return Fail("Could not estimate background motion after pan");

      var beforeTilt = await SnapshotAsync(cameraId, ct);
      if (beforeTilt is null)
        return Fail("Could not capture a snapshot before the tilt test");

      await PulseMoveAsync(cameraId, prep.ProfileToken, 0f, TestSpeed, ct);
      var afterTilt = await SnapshotAsync(cameraId, ct);
      await PulseMoveAsync(cameraId, prep.ProfileToken, 0f, -TestSpeed, ct);
      if (afterTilt is null)
        return Fail("Could not capture a snapshot after the tilt test");

      var tiltMotion = await EstimateMotionAsync(beforeTilt, afterTilt, ct);
      if (tiltMotion is null)
        return Fail("Could not estimate background motion after tilt");

      if (panMotion.Confidence < 0.35f || tiltMotion.Confidence < 0.35f)
        return Fail("Background motion confidence is too low — use a mostly static scene or try the wizard");

      // Judged on the measured shift, not on a normalized direction. dx/dy arrive as fractions of
      // the frame, so "did this test actually move the picture" is a question that can only be
      // asked before normalizing — afterwards every vector has length one, and an axis that did
      // not move is indistinguishable from one that swept across the frame.
      if (PtzCalibrationMath.IsTooSmall(panMotion.Dx, panMotion.Dy))
        return Fail(TooSmall("pan", panMotion.Dx, panMotion.Dy));
      if (PtzCalibrationMath.IsTooSmall(tiltMotion.Dx, tiltMotion.Dy))
        return Fail(TooSmall("tilt", tiltMotion.Dx, tiltMotion.Dy));

      if (PtzCalibrationMath.IsDegenerate(panMotion.Dx, panMotion.Dy, tiltMotion.Dx, tiltMotion.Dy))
        return Fail("Pan and tilt axes look too similar — calibration is unreliable");

      var panVector = PtzCalibrationMath.NormalizeVector(panMotion.Dx, panMotion.Dy);
      var tiltVector = PtzCalibrationMath.NormalizeVector(tiltMotion.Dx, tiltMotion.Dy);

      var saved = await SaveAsync(cameraId, panVector.Dx, panVector.Dy, tiltVector.Dx, tiltVector.Dy,
        MathF.Min(panMotion.Confidence, tiltMotion.Confidence), PtzCalibrationMethod.PtzCalibrationAuto, ct);
      return new PtzCalibrationRunResultDto { Success = true, Calibration = saved };
    }
    catch (InvalidOperationException ex)
    {
      logger.LogWarning(ex, "Auto PTZ calibration failed for camera '{Camera}'", cameraId);
      return Fail(ex.Message);
    }
    catch (RpcException ex)
    {
      logger.LogWarning(ex, "Auto PTZ calibration failed for camera '{Camera}'", cameraId);
      return Fail(ex.Status.Detail);
    }
    finally
    {
      await SafeStopAsync(cameraId, prep.ProfileToken, ct);
    }
  }

  public async Task<(string SessionId, PtzCalibrationWizardStepDto Step)> BeginWizardAsync(
    string cameraId, CancellationToken ct)
  {
    var prep = await PrepareAsync(cameraId, ct);
    if (prep.Error is not null)
      throw new InvalidOperationException(prep.Error);

    // Deliberately does not move the camera. The wizard used to pulse here and then ask which way
    // the picture went — by the time the operator had read the question the move was long over,
    // and there was no way to repeat it. The operator drives the axis with NudgeAsync instead, as
    // often as they need, and answers when they have seen it.
    var sessionId = wizardStore.Begin(cameraId, prep.ProfileToken);
    return (sessionId, new PtzCalibrationWizardStepDto { Axis = "pan", AwaitingAnswer = true });
  }

  public async Task<PtzCalibrationWizardStepDto> AnswerPanAsync(
    string sessionId, string cameraId, string direction, CancellationToken ct)
  {
    if (!wizardStore.TryGet(sessionId, out var sessionCameraId, out _, out _, out _))
      throw new InvalidOperationException("Calibration session expired or unknown");
    if (!string.Equals(sessionCameraId, cameraId, StringComparison.Ordinal))
      throw new InvalidOperationException("Camera id does not match the calibration session");

    var (dx, dy) = PtzCalibrationMath.DirectionToVector(direction);
    wizardStore.SetPanAnswer(sessionId, dx, dy);

    // No move here either: the operator decides when the tilt axis gets its pulse.
    return new PtzCalibrationWizardStepDto { Axis = "tilt", AwaitingAnswer = true };
  }

  public async Task<PtzCalibrationRunResultDto> AnswerTiltAsync(
    string sessionId, string cameraId, string direction, CancellationToken ct)
  {
    if (!wizardStore.TryGet(sessionId, out var sessionCameraId, out _, out var panDx, out var panDy))
      throw new InvalidOperationException("Calibration session expired or unknown");
    if (!string.Equals(sessionCameraId, cameraId, StringComparison.Ordinal))
      throw new InvalidOperationException("Camera id does not match the calibration session");
    if (panDx is null || panDy is null)
      throw new InvalidOperationException("Pan answer is missing");

    var (tiltDx, tiltDy) = PtzCalibrationMath.DirectionToVector(direction);
    if (PtzCalibrationMath.IsDegenerate(panDx.Value, panDy.Value, tiltDx, tiltDy))
    {
      wizardStore.Remove(sessionId);
      return Fail("Pan and tilt answers look too similar — pick more distinct directions");
    }

    var saved = await SaveAsync(
      cameraId, panDx.Value, panDy.Value, tiltDx, tiltDy, 0.8f,
      PtzCalibrationMethod.PtzCalibrationWizard, ct);
    wizardStore.Remove(sessionId);
    return new PtzCalibrationRunResultDto { Success = true, Calibration = saved };
  }

  /// <summary>
  /// One test pulse on one axis, asked for by the operator.
  /// </summary>
  /// <remarks>
  /// Repeatable on purpose: a single 350 ms pulse is easy to miss, and on a slow head it barely
  /// shifts the picture at all. Pressing the same button twice moves twice as far, and the
  /// opposite button brings the camera back, so the operator can rehearse the move until they are
  /// sure which way the picture went before answering.
  /// </remarks>
  /// <param name="axis">"pan" or "tilt".</param>
  /// <param name="positive">
  /// Which end of the axis to drive. The question the operator answers is always about the
  /// positive direction — the negative one exists to undo.
  /// </param>
  public async Task NudgeAsync(
    string sessionId, string cameraId, string axis, bool positive, CancellationToken ct)
  {
    if (!wizardStore.TryGet(sessionId, out var sessionCameraId, out var profileToken, out _, out _))
      throw new InvalidOperationException("Calibration session expired or unknown");
    if (!string.Equals(sessionCameraId, cameraId, StringComparison.Ordinal))
      throw new InvalidOperationException("Camera id does not match the calibration session");

    var speed = positive ? TestSpeed : -TestSpeed;
    var (pan, tilt) = axis.Trim().ToLowerInvariant() switch
    {
      "pan" => (speed, 0f),
      "tilt" => (0f, speed),
      _ => throw new ArgumentException($"Unknown axis '{axis}'"),
    };
    await PulseMoveAsync(cameraId, profileToken, pan, tilt, ct);
  }

  private async Task<(string? ProfileToken, string? Error)> PrepareAsync(string cameraId, CancellationToken ct)
  {
    var camera = await cameras.Get(cameraId, ct);
    if (camera is null)
      return (null, $"Camera '{cameraId}' is unknown");
    if (camera.HasTelemetry != true)
      return (null, $"Camera '{cameraId}' is not marked as having telemetry");

    try
    {
      var states = await tracking.ListTrackingStatesAsync(new ListTrackingStatesRequest(), Options(ct));
      if (states.States.Any(s => s.CameraId == cameraId && s.Active))
        return (null, "Stop object tracking before calibrating this camera");
    }
    catch (RpcException ex)
    {
      logger.LogWarning(ex, "Could not ask whether camera '{Camera}' is being tracked", cameraId);
      return (null, "Could not check whether object tracking is running");
    }

    // Empty is legitimate: VmsOnvif resolves the camera's first profile when none is named.
    var profileToken = camera.Streams
      .Select(s => s.ProfileToken)
      .FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty;
    return (profileToken, null);
  }

  private async Task<byte[]?> SnapshotAsync(string cameraId, CancellationToken ct)
  {
    await Task.Delay(SettleAfterStop, ct);
    var response = await (await onvif.OnvifAsync(cameraId, ct)).GetSnapshotAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));
    if (!string.IsNullOrEmpty(response.Error) || response.Data.IsEmpty)
      return null;
    return response.Data.ToByteArray();
  }

  private async Task PulseMoveAsync(
    string cameraId, string? profileToken, float pan, float tilt, CancellationToken ct)
  {
    var ptz = await onvif.PtzAsync(cameraId, ct);
    var move = await ptz.ContinuousMoveAsync(new PtzContinuousMoveRequest
    {
      SourceId = cameraId,
      ProfileToken = profileToken ?? string.Empty,
      Pan = pan,
      Tilt = tilt,
    }, Options(ct));
    if (!move.Success)
      throw new InvalidOperationException(move.Error);

    await Task.Delay(MovePulse, ct);
    await SafeStopAsync(cameraId, profileToken, ct);
  }

  private async Task SafeStopAsync(string cameraId, string? profileToken, CancellationToken ct)
  {
    try
    {
      var ptz = await onvif.PtzAsync(cameraId, ct);
      await ptz.StopAsync(new PtzStopRequest
      {
        SourceId = cameraId,
        ProfileToken = profileToken ?? string.Empty,
      }, Options(ct));
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "PTZ stop during calibration for camera '{Camera}' failed", cameraId);
    }
  }

  private async Task<EstimateGlobalMotionResponse?> EstimateMotionAsync(
    byte[] before, byte[] after, CancellationToken ct)
  {
    var response = await analytics.EstimateGlobalMotionAsync(
      new EstimateGlobalMotionRequest
      {
        BeforeJpeg = Google.Protobuf.ByteString.CopyFrom(before),
        AfterJpeg = Google.Protobuf.ByteString.CopyFrom(after),
      }, Options(ct));
    return response.Success ? response : null;
  }

  private async Task<PtzCalibrationDto> SaveAsync(
    string cameraId,
    float panDx,
    float panDy,
    float tiltDx,
    float tiltDy,
    float confidence,
    PtzCalibrationMethod method,
    CancellationToken ct)
  {
    (panDx, panDy) = PtzCalibrationMath.NormalizeVector(panDx, panDy);
    (tiltDx, tiltDy) = PtzCalibrationMath.NormalizeVector(tiltDx, tiltDy);

    var proto = new PtzCalibration
    {
      CameraId = cameraId,
      PanDx = panDx,
      PanDy = panDy,
      TiltDx = tiltDx,
      TiltDy = tiltDy,
      Confidence = confidence,
      Method = method,
      CalibratedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
    };
    await cfg.UpsertPtzCalibrationAsync(new UpsertPtzCalibrationRequest { Calibration = proto }, Options(ct));
    return ToDto(proto);
  }

  private static PtzCalibrationDto ToDto(PtzCalibration c) => new()
  {
    CameraId = c.CameraId,
    PanDx = c.PanDx,
    PanDy = c.PanDy,
    TiltDx = c.TiltDx,
    TiltDy = c.TiltDy,
    Confidence = c.Confidence,
    CalibratedAt = c.CalibratedAt?.ToDateTime(),
    Method = c.Method switch
    {
      PtzCalibrationMethod.PtzCalibrationWizard => "wizard",
      PtzCalibrationMethod.PtzCalibrationAuto => "auto",
      _ => "auto",
    },
  };

  private static PtzCalibrationRunResultDto Fail(string error) =>
    new() { Success = false, Error = error };

  /// Names the axis that did not move and by how much, so the operator can tell a camera that
  /// cannot tilt from a test pulse that was simply too short for this head.
  private static string TooSmall(string axis, float dx, float dy)
  {
    var shift = MathF.Sqrt((dx * dx) + (dy * dy));
    return $"The {axis} test moved the picture by only {shift * 100f:F1}% of the frame — too little "
         + "to tell which way that axis goes. Check that the camera really moves on this axis, then "
         + "try again or use the wizard.";
  }
}
