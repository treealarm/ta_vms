using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Vms.Cfg;
using VmsFs;

namespace VmsFs.Tests;

// CameraStorageCache and StorageListCache must never keep an answer that is not the present one: a
// failed call to VmsCfg is not "nothing assigned", and a fetch overtaken by an invalidation holds
// the state from before the change it was meant to follow.
public sealed class StorageCacheTests
{
  private const string Camera = "cam1";

  [Fact]
  public async Task A_failed_fetch_is_reported_and_not_cached_as_nothing_assigned()
  {
    var cfg = new FakeStorageClient();
    cfg.Assignments.Enqueue(() => throw new RpcException(new Status(StatusCode.Unavailable, "cfg down")));
    cfg.Assignments.Enqueue(() => Assigned("s1"));
    var cache = NewCache(cfg);

    await Assert.ThrowsAsync<RpcException>(() => cache.GetAllAsync(Camera, CancellationToken.None));
    var storages = await cache.GetAllAsync(Camera, CancellationToken.None);

    Assert.Equal("s1", Assert.Single(storages).StorageId);
    Assert.Equal(2, cfg.AssignmentCalls);
  }

  [Fact]
  public async Task A_fetch_overtaken_by_an_invalidation_is_answered_but_not_cached()
  {
    var cfg = new FakeStorageClient();
    var release = new TaskCompletionSource();
    cfg.Assignments.Enqueue(() => { release.Task.Wait(); return new GetCameraStoragesResponse(); });
    cfg.Assignments.Enqueue(() => Assigned("s1"));
    var cache = NewCache(cfg);

    var inFlight = Task.Run(() => cache.GetAllAsync(Camera, CancellationToken.None));
    await WaitUntilAsync(() => cfg.AssignmentCalls == 1);
    cache.Invalidate(Camera);
    release.SetResult();

    Assert.Empty(await inFlight);
    Assert.Equal("s1", Assert.Single(await cache.GetAllAsync(Camera, CancellationToken.None)).StorageId);
  }

  [Fact]
  public async Task A_fetch_overtaken_by_invalidating_everything_is_not_cached_either()
  {
    var cfg = new FakeStorageClient();
    var release = new TaskCompletionSource();
    cfg.Assignments.Enqueue(() => { release.Task.Wait(); return new GetCameraStoragesResponse(); });
    cfg.Assignments.Enqueue(() => Assigned("s1"));
    var cache = NewCache(cfg);

    var inFlight = Task.Run(() => cache.GetAllAsync(Camera, CancellationToken.None));
    await WaitUntilAsync(() => cfg.AssignmentCalls == 1);
    cache.InvalidateAll();
    release.SetResult();
    await inFlight;

    Assert.Single(await cache.GetAllAsync(Camera, CancellationToken.None));
  }

  [Fact]
  public async Task An_answer_nothing_overtook_is_cached()
  {
    var cfg = new FakeStorageClient();
    cfg.Assignments.Enqueue(() => Assigned("s1"));
    var cache = NewCache(cfg);

    await cache.GetAllAsync(Camera, CancellationToken.None);
    await cache.GetAllAsync(Camera, CancellationToken.None);

    Assert.Equal(1, cfg.AssignmentCalls);
  }

  [Fact]
  public async Task A_storage_list_overtaken_by_an_invalidation_is_not_cached()
  {
    var cfg = new FakeStorageClient();
    var release = new TaskCompletionSource();
    cfg.Lists.Enqueue(() => { release.Task.Wait(); return new ListStoragesResponse(); });
    cfg.Lists.Enqueue(() => Listed("s1"));
    var list = new StorageListCache(cfg, NullLogger<StorageListCache>.Instance);

    var inFlight = Task.Run(() => list.GetAsync(CancellationToken.None));
    await WaitUntilAsync(() => cfg.ListCalls == 1);
    list.Invalidate();
    release.SetResult();

    Assert.Empty(await inFlight);
    Assert.Single(await list.GetAsync(CancellationToken.None));
  }

  private static CameraStorageCache NewCache(FakeStorageClient cfg)
  {
    // The storage list always knows s1, so only the assignments decide the answer.
    cfg.Lists.Enqueue(() => Listed("s1"));
    return new CameraStorageCache(cfg, new StorageListCache(cfg, NullLogger<StorageListCache>.Instance),
      NullLogger<CameraStorageCache>.Instance);
  }

  private static GetCameraStoragesResponse Assigned(string storageId) =>
    new() { Assignments = { new CameraStorageAssignment { CameraId = Camera, StorageId = storageId } } };

  private static ListStoragesResponse Listed(string storageId) =>
    new() { Storages = { new Storage { StorageId = storageId, RootPath = "/tmp/" + storageId } } };

  private static async Task WaitUntilAsync(Func<bool> condition)
  {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    while (!condition())
    {
      if (DateTime.UtcNow > deadline) throw new TimeoutException();
      await Task.Delay(5);
    }
  }

  // Answers each call with the next scripted response; the last one repeats.
  private sealed class FakeStorageClient : StorageService.StorageServiceClient
  {
    public ConcurrentQueue<Func<GetCameraStoragesResponse>> Assignments { get; } = new();
    public ConcurrentQueue<Func<ListStoragesResponse>> Lists { get; } = new();
    private int _assignmentCalls, _listCalls;
    private Func<GetCameraStoragesResponse>? _lastAssignment;
    private Func<ListStoragesResponse>? _lastList;
    public int AssignmentCalls => Volatile.Read(ref _assignmentCalls);
    public int ListCalls => Volatile.Read(ref _listCalls);

    public override AsyncUnaryCall<GetCameraStoragesResponse> GetCameraStoragesAsync(
      CameraStoragesRequest request, CallOptions options)
    {
      Interlocked.Increment(ref _assignmentCalls);
      if (Assignments.TryDequeue(out var next)) _lastAssignment = next;
      return Call(_lastAssignment!);
    }

    public override AsyncUnaryCall<ListStoragesResponse> ListStoragesAsync(
      ListStoragesRequest request, CallOptions options)
    {
      Interlocked.Increment(ref _listCalls);
      if (Lists.TryDequeue(out var next)) _lastList = next;
      return Call(_lastList!);
    }

    private static AsyncUnaryCall<T> Call<T>(Func<T> answer) =>
      new(Task.Run(answer), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
  }
}
