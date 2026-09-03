namespace web_vms.Types.Dto;

public sealed class PtzCalibrationDto
{
  public string CameraId { get; set; } = string.Empty;
  public float PanDx { get; set; }
  public float PanDy { get; set; }
  public float TiltDx { get; set; }
  public float TiltDy { get; set; }
  public float Confidence { get; set; }
  public DateTime? CalibratedAt { get; set; }
  public string Method { get; set; } = "auto";
}

public sealed class PtzCalibrationWizardAnswerDto
{
  public string CameraId { get; set; } = string.Empty;
  public string Direction { get; set; } = string.Empty;
}

/// <summary>One operator-triggered test pulse during the wizard.</summary>
public sealed class PtzCalibrationNudgeDto
{
  public string CameraId { get; set; } = string.Empty;
  /// <summary>"pan" or "tilt".</summary>
  public string Axis { get; set; } = string.Empty;
  /// <summary>
  /// Which end of the axis to drive. The wizard's question is always about the positive
  /// direction; the negative one is there to put the camera back and try again.
  /// </summary>
  public bool Positive { get; set; } = true;
}

public sealed class PtzCalibrationRunResultDto
{
  public bool Success { get; set; }
  public string? Error { get; set; }
  public PtzCalibrationDto? Calibration { get; set; }
}

public sealed class PtzCalibrationWizardStepDto
{
  public string Axis { get; set; } = string.Empty;
  public bool AwaitingAnswer { get; set; }
}
