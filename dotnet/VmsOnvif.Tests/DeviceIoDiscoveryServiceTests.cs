using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using OnvifLib;
using VmsOnvif;

namespace VmsOnvif.Tests;

// Discovery is the manifest and the reconciliation: one snapshot per camera, and nothing at all
// from an answer that was not a list. The gRPC camera listing is not involved — the sweep of one
// camera is driven directly, which is the part with the decisions in it.
public class DeviceIoDiscoveryServiceTests
{
  private const string CameraId = "cam1";

  private static DeviceIoDiscoveryService Service(
    FakeReader reader, FakeSink sink, FakePublisher publisher) =>
    new(cfg: null!, reader, sink, publisher, "srv1", TimeSpan.FromMinutes(5),
        NullLogger<DeviceIoDiscoveryService>.Instance);

  [Fact]
  public async Task ADiscoveredCameraIsOneSnapshotWithEveryPart()
  {
    var reader = new FakeReader
    {
      Relays = [new OnvifRelayOutput("relay1", "bistable", "closed", 0)],
      Inputs = [new OnvifDigitalInput("input1", "open"), new OnvifDigitalInput("input2", "closed")],
    };
    var sink = new FakeSink();
    sink.Snapshots[("cam1", DeviceIoKind.Relay, "relay1")] =
      new DeviceIoSnapshot(DeviceIoKind.Relay, "relay1", true, "closed", null);
    sink.Snapshots[("cam1", DeviceIoKind.DigitalInput, "input1")] =
      new DeviceIoSnapshot(DeviceIoKind.DigitalInput, "input1", true, "open", "closed");
    var publisher = new FakePublisher();

    await Service(reader, sink, publisher).SweepCameraAsync(CameraId, default);

    var snapshot = Assert.Single(publisher.Snapshots);
    Assert.Equal(CameraId, snapshot.SourceId);
    Assert.True(snapshot.Reachable);
    Assert.Equal(3, snapshot.Parts.Count);

    var relay = Assert.Single(snapshot.Parts, p => p.Kind == "relay");
    Assert.Equal(true, relay.Active);

    var input = Assert.Single(snapshot.Parts, p => p.Token == "input1");
    Assert.Equal("closed", input.Contact);

    // Both lists were reported, which is what opens the watcher without an operator.
    Assert.Equal(2, sink.Reports.Count);
  }

  [Fact]
  public async Task AFailedInputReadPublishesNoSnapshotAndLeavesTheInputsAlone()
  {
    var reader = new FakeReader
    {
      Relays = [new OnvifRelayOutput("relay1", "bistable", "closed", 0)],
      InputsThrow = true,
    };
    var sink = new FakeSink();
    var publisher = new FakePublisher();

    await Service(reader, sink, publisher).SweepCameraAsync(CameraId, default);

    // A snapshot from a partial read would say the inputs are gone; they are not, the camera just
    // did not answer.
    Assert.Empty(publisher.Snapshots);
    var report = Assert.Single(sink.Reports);
    Assert.Equal(DeviceIoKind.Relay, report.Kind);
  }

  [Fact]
  public async Task ARecoveredCameraIsReachableAgain()
  {
    var reader = new FakeReader { InputsThrow = true };
    var sink = new FakeSink();
    var publisher = new FakePublisher();
    var service = Service(reader, sink, publisher);

    await service.SweepCameraAsync(CameraId, default);
    await service.SweepCameraAsync(CameraId, default);
    await service.SweepCameraAsync(CameraId, default);
    Assert.False(publisher.Snapshots.Single().Reachable);

    reader.InputsThrow = false;
    await service.SweepCameraAsync(CameraId, default);

    Assert.True(publisher.Snapshots.Last().Reachable);
  }

  [Fact]
  public async Task ThreeFailedSweepsReportTheCameraUnreachableWithoutParts()
  {
    var reader = new FakeReader { RelaysThrow = true, InputsThrow = true };
    var sink = new FakeSink();
    var publisher = new FakePublisher();
    var service = Service(reader, sink, publisher);

    await service.SweepCameraAsync(CameraId, default);
    await service.SweepCameraAsync(CameraId, default);
    Assert.Empty(publisher.Snapshots);

    await service.SweepCameraAsync(CameraId, default);

    var snapshot = Assert.Single(publisher.Snapshots);
    Assert.False(snapshot.Reachable);
    // Empty on purpose: only a successful answer may say a part is absent.
    Assert.Empty(snapshot.Parts);
    Assert.Empty(sink.Reports);
  }

  private sealed class FakeReader : IDeviceIoReader
  {
    public List<OnvifRelayOutput> Relays { get; init; } = [];
    public List<OnvifDigitalInput> Inputs { get; init; } = [];
    public bool RelaysThrow { get; set; }
    public bool InputsThrow { get; set; }

    public Task<List<OnvifRelayOutput>> GetRelayOutputsAsync(string cameraId, CancellationToken ct) =>
      RelaysThrow ? throw new TimeoutException("no answer") : Task.FromResult(Relays);

    public Task<List<OnvifDigitalInput>> GetDigitalInputsAsync(string cameraId, CancellationToken ct) =>
      InputsThrow ? throw new TimeoutException("no answer") : Task.FromResult(Inputs);
  }

  private sealed class FakeSink : IDeviceIoStateSink
  {
    public List<(DeviceIoKind Kind, IReadOnlyList<DeviceDescriptor> Devices)> Reports { get; } = [];
    public Dictionary<(string, DeviceIoKind, string), DeviceIoSnapshot> Snapshots { get; } = [];

    public Task ReportDevicesAsync(
      string cameraId, DeviceIoKind kind, IReadOnlyList<DeviceDescriptor> devices)
    {
      Reports.Add((kind, devices));
      return Task.CompletedTask;
    }

    public DeviceIoSnapshot? TryGetSnapshot(string cameraId, DeviceIoKind kind, string token) =>
      Snapshots.GetValueOrDefault((cameraId, kind, token));
  }

  private sealed class FakePublisher : IDeviceSnapshotPublisher
  {
    public ConcurrentQueue<DeviceSnapshotPayload> Snapshots { get; } = new();

    public Task PublishAsync(DeviceSnapshotPayload snapshot)
    {
      Snapshots.Enqueue(snapshot);
      return Task.CompletedTask;
    }
  }
}
