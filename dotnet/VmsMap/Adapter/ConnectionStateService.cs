using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VmsMap.Clients;

namespace VmsMap.Adapter
{
  // Applies one poll's worth of media-path answers and repairs an unplaced camera's parent. Kept
  // apart from the poller so the decisions can be tested without a timer or a Dapr sidecar.
  internal sealed class ConnectionStateService
  {
    private readonly CameraStateWriter _stateWriter;
    private readonly DeviceNodeService _nodes;

    public ConnectionStateService(CameraStateWriter stateWriter, DeviceNodeService nodes)
    {
      _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
      _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
    }

    public async Task ApplyAsync(
      IReadOnlyDictionary<string, bool> connected, CancellationToken ct)
    {
      // A camera absent from the answer could not be asked about; its last known state stands.
      foreach (var (cameraId, isConnected) in connected)
        await _stateWriter.SetConnectedAsync(cameraId, isConnected, ct);
    }

    public async Task ReconcileAsync(
      IReadOnlyCollection<CameraInfo> cameras, CancellationToken ct)
    {
      foreach (var camera in cameras)
        await _nodes.ReconcileParentAsync(camera, ct);
    }
  }
}
