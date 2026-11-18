using Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using OnvifLib;
using web_srv.Types.Actors;

namespace VmsOnvifActors.Tests;

/// A PTZ command reports failure as a value. A camera that cannot be reached is the ordinary state
/// of the network, and an exception leaving an actor method is what the host logs, with its stack,
/// on every command the tracker sends — 69 of them in 150 seconds on the bench.
public class CameraActorPtzCommandTests
{
  // A closed loopback port: discovery fails at once, without a camera and without leaving the box.
  private const string CameraIp = "127.0.0.1";
  private const int CameraPort = 1;
  private const string ProfileToken = "profile_1";

  private static readonly string CameraAddress = $"{CameraIp}:{CameraPort}";
  private static readonly CameraConnInfo Camera = new(CameraIp, CameraPort, "user", "pass");

  [Fact]
  public async Task AnAbsoluteMoveToAnUnreachableCameraFailsWithoutThrowing()
  {
    var actor = NewActor();

    var result = await actor.PtzAbsoluteMoveAsync(
      new PtzAbsoluteMoveActorRequest(Camera, ProfileToken, 0f, 0f, 0f, 0.5f));

    AssertNamesTheUnreachableCamera(result);
  }

  [Fact]
  public async Task AContinuousMoveToAnUnreachableCameraFailsWithoutThrowing()
  {
    var actor = NewActor();

    var result = await actor.PtzContinuousMoveAsync(
      new PtzMoveActorRequest(Camera, ProfileToken, 0f, 0f, 0f));

    AssertNamesTheUnreachableCamera(result);
  }

  [Fact]
  public async Task ARelativeMoveToAnUnreachableCameraFailsWithoutThrowing()
  {
    var actor = NewActor();

    var result = await actor.PtzRelativeMoveAsync(
      new PtzMoveActorRequest(Camera, ProfileToken, 0f, 0f, 0f));

    AssertNamesTheUnreachableCamera(result);
  }

  [Fact]
  public async Task AStopOfAnUnreachableCameraFailsWithoutThrowing()
  {
    var actor = NewActor();

    var result = await actor.PtzStopAsync(new PtzStopActorRequest(Camera, ProfileToken));

    AssertNamesTheUnreachableCamera(result);
  }

  [Fact]
  public async Task ACommandInsideTheUnavailableWindowFailsTheSameWay()
  {
    var actor = NewActor();
    var request = new PtzStopActorRequest(Camera, ProfileToken);
    var first = await actor.PtzStopAsync(request);

    var second = await actor.PtzStopAsync(request);

    AssertNamesTheUnreachableCamera(second);
    Assert.Equal(first.Error, second.Error);
  }

  private static CameraActor NewActor() =>
    new(ActorHost.CreateForTest<CameraActor>(), new CameraActorDomain("vms"),
      new OnvifLoggerAdapter(NullLogger.Instance), TimeProvider.System);

  private static void AssertNamesTheUnreachableCamera(PtzCommandActorResult result)
  {
    Assert.False(result.Success);
    Assert.Contains(CameraAddress, result.Error);
  }
}
