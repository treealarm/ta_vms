using Grpc.Core;
using Vms.Analytics;
using Vms.Cfg;
using Vms.Onvif;
using Vms.PtzTracking;
using web_vms.Types.Dto;

namespace web_vms;

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

      var panVector = PtzCalibrationMath.NormalizeVector(panMotion.Dx, panMotion.Dy);
      var tiltVector = PtzCalibrationMath.NormalizeVector(tiltMotion.Dx, tiltMotion.Dy);

      if (PtzCalibrationMath.IsDegenerate(panVector.Dx, panVector.Dy, tiltVector.Dx, tiltVector.Dy))
        return Fail("Pan and tilt axes look too similar — calibration is unreliable");

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

    var sessionId = wizardStore.Begin(cameraId, prep.ProfileToken);
    await PulseMoveAsync(cameraId, prep.ProfileToken, TestSpeed, 0f, ct);
    return (sessionId, new PtzCalibrationWizardStepDto { Axis = "pan", AwaitingAnswer = true });
  }

  public async Task<PtzCalibrationWizardStepDto> AnswerPanAsync(
    string sessionId, string cameraId, string direction, CancellationToken ct)
  {
    if (!wizardStore.TryGet(sessionId, out var sessionCameraId, out var profileToken, out _, out _))
      throw new InvalidOperationException("Calibration session expired or unknown");
    if (!string.Equals(sessionCameraId, cameraId, StringComparison.Ordinal))
      throw new InvalidOperationException("Camera id does not match the calibration session");

    var (dx, dy) = PtzCalibrationMath.DirectionToVector(direction);
    wizardStore.SetPanAnswer(sessionId, dx, dy);

    await PulseMoveAsync(cameraId, profileToken, -TestSpeed, 0f, ct);
    await PulseMoveAsync(cameraId, profileToken, 0f, TestSpeed, ct);
    return new PtzCalibrationWizardStepDto { Axis = "tilt", AwaitingAnswer = true };
  }

  public async Task<PtzCalibrationRunResultDto> AnswerTiltAsync(
    string sessionId, string cameraId, string direction, CancellationToken ct)
  {
    if (!wizardStore.TryGet(sessionId, out var sessionCameraId, out var profileToken, out var panDx, out var panDy))
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
    await PulseMoveAsync(cameraId, profileToken, 0f, -TestSpeed, ct);
    return new PtzCalibrationRunResultDto { Success = true, Calibration = saved };
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
}
