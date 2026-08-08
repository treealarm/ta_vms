using Dapr.Actors;

namespace web_vms.Types.Actors;

public interface ICameraActor : IActor
{
  Task<List<ProfileInfoDto>> GetProfilesAsync(CameraConnInfo conn);
  Task<GetStreamUriActorResult> GetStreamUriAsync(GetStreamUriActorRequest request);
  Task PtzContinuousMoveAsync(PtzMoveActorRequest request);
  Task PtzAbsoluteMoveAsync(PtzAbsoluteMoveActorRequest request);
  Task PtzRelativeMoveAsync(PtzMoveActorRequest request);
  Task PtzStopAsync(PtzStopActorRequest request);
  Task<PtzCapabilitiesDto> GetPtzCapabilitiesAsync(CameraConnInfo conn);
  Task<SnapshotActorResult> GetSnapshotAsync(CameraConnInfo conn);
}

/// Domain is the node whose camera subnet this connection belongs to. The actor checks it against
/// its own: distinct actor type names already keep a call from reaching another node's host, but two
/// nodes accidentally configured with the same OWN_SERVER_ID would share one ring, and a camera
/// handled from the wrong side of the network looks exactly like a camera that stopped answering.
public record CameraConnInfo(string Ip, int Port, string User, string Pass, string Xaddr = "", string Domain = "");
public record ProfileInfoDto(string Token, string Name, int Width, int Height, string Encoding, string VideoSourceToken = "");
public record GetStreamUriActorRequest(CameraConnInfo Conn, string ProfileToken);
public record GetStreamUriActorResult(string Uri);
public record SnapshotActorResult(byte[] Data, string? MimeType);

public record PtzMoveActorRequest(CameraConnInfo Conn, string ProfileToken, float Pan, float Tilt, float Zoom);
public record PtzAbsoluteMoveActorRequest(CameraConnInfo Conn, string ProfileToken, float Pan, float Tilt, float Zoom, float Speed);
public record PtzStopActorRequest(CameraConnInfo Conn, string ProfileToken);
public record PtzCapabilitiesDto(bool AbsoluteMove, bool RelativeMove, bool ContinuousMove);
