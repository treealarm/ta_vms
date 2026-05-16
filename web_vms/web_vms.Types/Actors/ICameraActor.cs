using Dapr.Actors;

namespace web_vms.Types.Actors;

public interface ICameraActor : IActor
{
  Task<List<ProfileInfoDto>> GetProfilesAsync(CameraConnInfo conn);
  Task<GetStreamUriActorResult> GetStreamUriAsync(GetStreamUriActorRequest request);
}

public record CameraConnInfo(string Ip, int Port, string User, string Pass);
public record ProfileInfoDto(string Token, string Name, int Width, int Height, string Encoding);
public record GetStreamUriActorRequest(CameraConnInfo Conn, string ProfileToken);
public record GetStreamUriActorResult(string Uri);
