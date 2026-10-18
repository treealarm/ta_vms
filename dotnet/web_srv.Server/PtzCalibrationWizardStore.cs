using System.Collections.Concurrent;
using Vms.Cfg;

namespace web_srv;

/// <summary>In-memory state for the interactive PTZ calibration wizard.</summary>
public sealed class PtzCalibrationWizardStore
{
  private sealed record Session(
    string CameraId,
    string? ProfileToken,
    float? PanDx,
    float? PanDy,
    DateTime CreatedAt);

  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
  private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

  public string Begin(string cameraId, string? profileToken)
  {
    var id = Guid.NewGuid().ToString("N");
    _sessions[id] = new Session(cameraId, profileToken, null, null, DateTime.UtcNow);
    return id;
  }

  public bool TryGet(string sessionId, out string cameraId, out string? profileToken, out float? panDx, out float? panDy)
  {
    cameraId = string.Empty;
    profileToken = null;
    panDx = null;
    panDy = null;
    if (!_sessions.TryGetValue(sessionId, out var session))
      return false;
    if (DateTime.UtcNow - session.CreatedAt > Ttl)
    {
      _sessions.TryRemove(sessionId, out _);
      return false;
    }
    cameraId = session.CameraId;
    profileToken = session.ProfileToken;
    panDx = session.PanDx;
    panDy = session.PanDy;
    return true;
  }

  public void SetPanAnswer(string sessionId, float panDx, float panDy)
  {
    if (!_sessions.TryGetValue(sessionId, out var session))
      return;
    _sessions[sessionId] = session with { PanDx = panDx, PanDy = panDy };
  }

  public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);
}
