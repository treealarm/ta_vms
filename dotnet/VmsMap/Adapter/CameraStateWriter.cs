using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Adapter
{
  // The camera-spelled wrappers over ObjectStateWriter: the alarm adapter and the connectivity
  // poller both act on a camera id, and deriving its canonical node here keeps that derivation in
  // one place. Anything that writes a state set goes through ObjectStateWriter, which owns the
  // lock and the transaction.
  internal sealed class CameraStateWriter
  {
    public const string Alarm = "ALARM";
    public const string Norm = "NORM";
    public const string Connected = "CONNECTED";
    public const string Disconnected = "DISCONNECTED";
    /// <summary>Reachable, but no configured stream is delivering frames. Not an alarm.</summary>
    public const string NoVideo = "NO_VIDEO";

    private readonly ObjectStateWriter _writer;

    public CameraStateWriter(ObjectStateWriter writer)
    {
      _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public Task SetAlarmAsync(string cameraId, CancellationToken ct) =>
      ChangeAsync(cameraId, add: [Alarm], remove: [Norm], ct);

    public Task ClearAlarmAsync(string cameraId, CancellationToken ct) =>
      ChangeAsync(cameraId, add: [Norm], remove: [Alarm], ct);

    /// <summary>
    /// Writes the presence states in one operation: <c>connected == null</c> leaves the connection
    /// as it is, <c>noVideo == null</c> leaves the video state as it is. Both null changes nothing.
    /// </summary>
    public Task<bool> ApplyPresenceAsync(
      string cameraId, bool? connected, bool? noVideo, CancellationToken ct)
    {
      var add = new List<string>(2);
      var remove = new List<string>(2);

      if (connected == true)
      {
        add.Add(Connected);
        remove.Add(Disconnected);
      }
      else if (connected == false)
      {
        add.Add(Disconnected);
        remove.Add(Connected);
      }

      if (noVideo == true)
        add.Add(NoVideo);
      else if (noVideo == false)
        remove.Add(NoVideo);

      if (add.Count == 0 && remove.Count == 0)
        return Task.FromResult(false);

      return ChangeAsync(cameraId, add, remove, ct);
    }

    private Task<bool> ChangeAsync(
      string cameraId,
      IReadOnlyCollection<string> add,
      IReadOnlyCollection<string> remove,
      CancellationToken ct) =>
      _writer.ChangeAsync(CameraObjectId.For(cameraId), add, remove, ct);
  }
}
