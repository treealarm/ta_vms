using Microsoft.AspNetCore.Mvc;
using web_vms.Authorization;
using web_vms.Types.Dto;

namespace web_vms;

[ApiController]
[Route("api/ptz-calibration")]
public class PtzCalibrationController(PtzCalibrationService service) : ControllerBase
{
  [HttpGet("{cameraId}")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> Get(string cameraId, CancellationToken ct)
  {
    var calibration = await service.GetAsync(cameraId, ct);
    return calibration is null ? NotFound() : Ok(calibration);
  }

  [HttpDelete("{cameraId}")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> Remove(string cameraId, CancellationToken ct)
  {
    var removed = await service.RemoveAsync(cameraId, ct);
    return removed ? Ok() : NotFound();
  }

  [HttpPost("{cameraId}/auto")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> RunAuto(string cameraId, CancellationToken ct)
  {
    var result = await service.RunAutoAsync(cameraId, ct);
    return result.Success ? Ok(result) : BadRequest(result);
  }

  [HttpPost("{cameraId}/wizard/begin")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> BeginWizard(string cameraId, CancellationToken ct)
  {
    try
    {
      var (sessionId, step) = await service.BeginWizardAsync(cameraId, ct);
      return Ok(new { sessionId, step });
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new PtzCalibrationRunResultDto { Success = false, Error = ex.Message });
    }
  }

  [HttpPost("wizard/{sessionId}/nudge")]
  [CameraAccess(CameraAction.Ptz, Source = CameraIdSource.Body)]
  public async Task<IActionResult> Nudge(
    string sessionId, [FromBody] PtzCalibrationNudgeDto dto, CancellationToken ct)
  {
    try
    {
      await service.NudgeAsync(sessionId, dto.CameraId, dto.Axis, dto.Positive, ct);
      return Ok();
    }
    catch (ArgumentException ex)
    {
      return BadRequest(ex.Message);
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(ex.Message);
    }
  }

  [HttpPost("wizard/{sessionId}/pan")]
  [CameraAccess(CameraAction.Ptz, Source = CameraIdSource.Body)]
  public async Task<IActionResult> AnswerPan(
    string sessionId, [FromBody] PtzCalibrationWizardAnswerDto dto, CancellationToken ct)
  {
    try
    {
      var step = await service.AnswerPanAsync(sessionId, dto.CameraId, dto.Direction, ct);
      return Ok(step);
    }
    catch (ArgumentException ex)
    {
      return BadRequest(ex.Message);
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(ex.Message);
    }
  }

  [HttpPost("wizard/{sessionId}/tilt")]
  [CameraAccess(CameraAction.Ptz, Source = CameraIdSource.Body)]
  public async Task<IActionResult> AnswerTilt(
    string sessionId, [FromBody] PtzCalibrationWizardAnswerDto dto, CancellationToken ct)
  {
    try
    {
      var result = await service.AnswerTiltAsync(sessionId, dto.CameraId, dto.Direction, ct);
      return result.Success ? Ok(result) : BadRequest(result);
    }
    catch (ArgumentException ex)
    {
      return BadRequest(ex.Message);
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(ex.Message);
    }
  }
}
