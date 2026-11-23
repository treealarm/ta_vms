using System.Collections.Concurrent;
using System.Net.Sockets;
using web_srv.Types.Actors;

namespace VmsOnvif;

public enum ReachabilityResult
{
  /// <summary>Could not be asked at all: VmsCfg or routing failed. Not an answer from the camera.</summary>
  Unknown,
  Reachable,
  Unreachable,
  /// <summary>No ONVIF endpoint configured — a missing channel, not a refusal.</summary>
  NoOnvif,
}

/// <summary>Whether the camera answers a cheap ONVIF call at all.</summary>
public interface IReachabilityReader
{
  Task<bool> AnswersAsync(CameraConnInfo conn, CancellationToken ct);
}

/// <summary>Whether the camera's ONVIF port accepts a TCP connection.</summary>
public interface INetworkProbe
{
  Task<bool> AcceptsConnectionAsync(CameraConnInfo conn, CancellationToken ct);
}

/// <summary>
/// Answers whether a camera is present on the network — not whether an ONVIF operation succeeded.
/// </summary>
/// <remarks>
/// A camera that answers with 401, a SOAP fault or "operation not supported" is on the network just
/// as much as one that answers normally. So a failed ONVIF call is followed by a plain TCP connect
/// to the same address: a device that accepts the connection is reachable, and only a refused
/// connection says otherwise. The map calls this only for cameras whose video is known to be gone,
/// so a healthy camera costs no extra ONVIF request.
/// </remarks>
public sealed class CameraReachabilityProbe(
  ICameraConnectionSource connections,
  IReachabilityReader reader,
  INetworkProbe network,
  TimeProvider clock,
  ILogger<CameraReachabilityProbe> logger)
{
  private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
  private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(3);
  private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);

  private readonly ConcurrentDictionary<string, (ReachabilityResult Result, DateTimeOffset AtUtc)> _cache = new();

  // One probe per camera in flight: the sweep and a device event may ask about the same camera at
  // the same moment, and OnvifLib has no cancellation of its own.
  private readonly ConcurrentDictionary<string, Lazy<Task<ReachabilityResult>>> _inFlight = new();

  public async Task<ReachabilityResult> ProbeAsync(string cameraId, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return ReachabilityResult.Unknown;

    if (_cache.TryGetValue(cameraId, out var cached)
        && clock.GetUtcNow() - cached.AtUtc < CacheTtl)
    {
      return cached.Result;
    }

    var probe = _inFlight.GetOrAdd(
      cameraId,
      id => new Lazy<Task<ReachabilityResult>>(() => ProbeOnceAsync(id)));
    try
    {
      var result = await probe.Value.WaitAsync(ct);
      // "Could not ask" is not a fact about the camera; it is retried on the next call.
      if (result != ReachabilityResult.Unknown)
        _cache[cameraId] = (result, clock.GetUtcNow());
      return result;
    }
    finally
    {
      _inFlight.TryRemove(cameraId, out _);
    }
  }

  private async Task<ReachabilityResult> ProbeOnceAsync(string cameraId)
  {
    using var resolve = new CancellationTokenSource(ResolveTimeout);
    var data = await connections.GetAsync(cameraId, resolve.Token);
    if (data is null)
      return ReachabilityResult.Unknown;
    if (data.Conn is null)
      return ReachabilityResult.NoOnvif;

    try
    {
      using var answer = new CancellationTokenSource(AnswerTimeout);
      if (await reader.AnswersAsync(data.Conn, answer.Token))
        return ReachabilityResult.Reachable;
    }
    catch (Exception ex)
    {
      logger.LogDebug(ex, "ONVIF did not answer for camera '{CameraId}'", cameraId);
    }

    // The operation failed — but the device may simply have rejected it. Only a refused
    // connection says the camera is not there.
    try
    {
      using var connect = new CancellationTokenSource(ConnectTimeout);
      if (await network.AcceptsConnectionAsync(data.Conn, connect.Token))
        return ReachabilityResult.Reachable;
    }
    catch (Exception ex)
    {
      logger.LogDebug(ex, "Could not reach camera '{CameraId}' at its ONVIF address", cameraId);
    }

    return ReachabilityResult.Unreachable;
  }
}

/// <summary>The network half of presence: does the camera's ONVIF address accept a connection?</summary>
public sealed class TcpNetworkProbe : INetworkProbe
{
  public async Task<bool> AcceptsConnectionAsync(CameraConnInfo conn, CancellationToken ct)
  {
    try
    {
      using var client = new TcpClient();
      await client.ConnectAsync(conn.Ip, conn.Port, ct);
      return true;
    }
    catch
    {
      return false;
    }
  }
}
