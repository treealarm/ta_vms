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

    private readonly ObjectStateWriter _writer;

    public CameraStateWriter(ObjectStateWriter writer)
    {
      _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public Task SetAlarmAsync(string cameraId, CancellationToken ct) =>
      ChangeAsync(cameraId, add: [Alarm], remove: [Norm], ct);

    public Task ClearAlarmAsync(string cameraId, CancellationToken ct) =>
      ChangeAsync(cameraId, add: [Norm], remove: [Alarm], ct);

    public Task SetConnectedAsync(string cameraId, bool connected, CancellationToken ct) =>
      ChangeAsync(
        cameraId,
        add: [connected ? Connected : Disconnected],
        remove: [connected ? Disconnected : Connected],
        ct);

    private Task ChangeAsync(
      string cameraId,
      IReadOnlyCollection<string> add,
      IReadOnlyCollection<string> remove,
      CancellationToken ct) =>
      _writer.ChangeAsync(CameraObjectId.For(cameraId), add, remove, ct);
  }
}
