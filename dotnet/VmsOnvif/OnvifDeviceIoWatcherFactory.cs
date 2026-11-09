using OnvifLib;
using web_srv.Types.Actors;

namespace VmsOnvif;

/// <summary>
/// Opens a camera's Device/Trigger subscription, on a camera and event service of its own.
/// </summary>
/// <remarks>
/// The watcher takes over the event service it is given — its topic filter becomes the device
/// topics — so it needs one nothing else is pulling from. A camera of its own also keeps the device
/// topics away from the camera's ordinary event subscription, which must stay on every topic the
/// operator asked for.
/// </remarks>
public sealed class OnvifDeviceIoWatcherFactory(
  IOnvifLogger onvifLogger,
  ILogger<OnvifDeviceIoWatcherFactory> logger) : IDeviceIoWatcherFactory
{
  public async Task<IDeviceIoWatcher?> CreateAsync(CameraConnInfo conn)
  {
    // Xaddr must be passed (as OnvifSettingsService does): on cameras whose device service is not
    // at the default /onvif/device_service path, deriving the URL from ip:port resolves no
    // services at all.
    var camera = Camera.Create(
      conn.Ip, conn.Port, conn.User, conn.Pass, logger: onvifLogger, xaddr: conn.Xaddr);

    var events = await camera.GetEventService();
    if (events == null)
    {
      logger.LogInformation(
        "Camera {Ip}:{Port} has no ONVIF event service — live device I/O state is unavailable",
        conn.Ip, conn.Port);
      return null;
    }

    try
    {
      var watcher = await DeviceIoStateWatcher.CreateAsync(events, onvifLogger);
      return new OnvifDeviceIoWatcher(watcher, events);
    }
    catch
    {
      // The subscription the service created on the way in still has to be released, even though
      // the watcher never got to take it over.
      await events.DisposeAsync();
      throw;
    }
  }

  private sealed class OnvifDeviceIoWatcher(DeviceIoStateWatcher watcher, EventService1 events)
    : IDeviceIoWatcher
  {
    public event Action<DeviceIoState>? StateChanged
    {
      add => watcher.StateChanged += value;
      remove => watcher.StateChanged -= value;
    }

    public IReadOnlyCollection<DeviceIoState> States => watcher.States;

    public async ValueTask DisposeAsync()
    {
      // The watcher first: stopping it releases its pull point, and the service must still be open
      // for that. The service is not disposed by the watcher — its owner still has to do it, and
      // Disposing these two in the other order would close the subscription manager first.
      await watcher.DisposeAsync();
      await events.DisposeAsync();
    }
  }
}
