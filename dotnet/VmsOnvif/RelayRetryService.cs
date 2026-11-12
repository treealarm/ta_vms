namespace VmsOnvif;

/// <summary>
/// Repeats relay commands the camera has not confirmed yet, and lets go of the ones it never will.
/// </summary>
/// <remarks>
/// A single SetRelayOutputState call can be lost — a timed-out SOAP request, a firmware that drops
/// one in a while — and the operator has no way to tell, because ONVIF has no getter for a relay's
/// state: only the camera's Device/Trigger report proves the command landed. The wish itself lives
/// in <see cref="DeviceIoStateService"/>; this loop only gives it a clock.
/// </remarks>
public sealed class RelayRetryService(DeviceIoStateService state) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(DeviceIoStateService.RetryInterval);
    try
    {
      while (await timer.WaitForNextTickAsync(stoppingToken))
        await state.RetryPendingAsync(stoppingToken);
    }
    catch (OperationCanceledException)
    {
      // Shutting down: an unconfirmed wish does not survive the process anyway.
    }
  }
}
