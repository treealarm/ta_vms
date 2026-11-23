using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsMap.Clients;

namespace VmsMap.Adapter
{
  /// <summary>
  /// Follows the cameras whose place on the map is their own device's to report.
  /// </summary>
  /// <remarks>
  /// Only the point of the canonical node moves, and only when it moved more than half a metre:
  /// a camera that answers the same coordinate every five seconds must not fill the map's watch
  /// stream with updates that change nothing. A camera without the flag is never asked, because a
  /// hand-placed camera is calibrated and aimed by survey, and drifting it would invalidate both.
  /// </remarks>
  internal sealed class CameraGeoFollower : BackgroundService
  {
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>Below this a move is not worth a tree write; ENU metres, the same frame as the map.</summary>
    internal const double MoveThresholdMeters = 0.5;

    /// <summary>Below this a turn is not worth a tree write, in degrees.</summary>
    internal const double TurnThresholdDeg = 2;

    private readonly IServiceScopeFactory _scopes;
    private readonly ICameraCatalog _catalog;
    private readonly ICameraGeoSource _source;
    private readonly ILogger<CameraGeoFollower> _logger;

    // The last point written per camera, so a still camera costs no writes. In memory on purpose:
    // after a restart the first answer is compared against the point the map still holds, and only
    // one that disagrees with it is written — so a downtime that really moved a camera is corrected,
    // and one that did not costs a read instead of a write.
    private readonly ConcurrentDictionary<string, CameraGeoPoint> _lastWritten =
      new(StringComparer.Ordinal);

    public CameraGeoFollower(
      IServiceScopeFactory scopes,
      ICameraCatalog catalog,
      ICameraGeoSource source,
      ILogger<CameraGeoFollower> logger)
    {
      _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
      _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
      _source = source ?? throw new ArgumentNullException(nameof(source));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
      while (!stoppingToken.IsCancellationRequested)
      {
        try
        {
          await PollOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
          return;
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "Following the cameras' device positions failed");
        }

        try
        {
          await Task.Delay(Interval, stoppingToken);
        }
        catch (OperationCanceledException)
        {
          return;
        }
      }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
      var cameras = await _catalog.ListAsync(null, ct);
      var followed = cameras.Where(c => c.GeoFromDevice).ToList();

      // A camera that stopped following — or stopped existing — is forgotten, so a later re-enable
      // starts from the map's own point again rather than a remembered one that may be stale.
      var followedIds = followed.Select(c => c.CameraId).ToHashSet(StringComparer.Ordinal);
      foreach (var cameraId in _lastWritten.Keys.Where(id => !followedIds.Contains(id)).ToArray())
        _lastWritten.TryRemove(cameraId, out _);

      if (followed.Count == 0)
        return;

      using var scope = _scopes.CreateScope();
      var writer = scope.ServiceProvider.GetRequiredService<ICameraGeoWriter>();

      foreach (var camera in followed)
      {
        var point = await _source.GetAsync(camera, ct);
        if (point is not { } position)
          continue;

        if (_lastWritten.TryGetValue(camera.CameraId, out var previous))
        {
          // A camera standing still but turning — the one on a vehicle — is a change worth writing
          // too: the map draws its view from the heading, and a sector left pointing where the car
          // was facing a minute ago is worse than no sector at all.
          if (IsStill(previous, position))
            continue;
        }
        else if (await writer.GetReferencePointAsync(camera.CameraId, ct) is { } stored)
        {
          // The memory is empty after a restart, so this is the first answer of the run. The row
          // the map holds is what the last run left there; an answer that agrees with it is not a
          // move, and writing it would cost a tree write and a watch-stream update for nothing.
          // The read is the price, paid once per camera per restart.
          if (IsStill(stored, position))
          {
            _lastWritten[camera.CameraId] = stored;
            continue;
          }
        }

        if (!await writer.MoveReferencePointAsync(camera.CameraId, position.Lat, position.Lon, position.HeadingDeg, ct))
          continue; // no reference placement — nothing to move. Retried when one appears.

        _lastWritten[camera.CameraId] = position;
        _logger.LogInformation(
          "Camera '{CameraId}' moved to {Lat:F6},{Lon:F6} heading {Heading} as reported by the device",
          camera.CameraId, position.Lat, position.Lon,
          position.HeadingDeg is { } heading ? $"{heading:F0}°" : "(not reported)");
      }
    }

    /// <summary>
    /// What counts as the same place: below both thresholds, nothing is worth a tree write.
    /// </summary>
    private static bool IsStill(CameraGeoPoint known, CameraGeoPoint reported) =>
      known.DistanceMetersTo(reported) < MoveThresholdMeters
      && known.HeadingDeltaDegTo(reported) < TurnThresholdDeg;
  }
}
