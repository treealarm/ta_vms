using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Vms.Cfg;
using VmsMap.Clients;

namespace VmsMap.Tests;

// The settings cache is what makes a part's name visible to the arming resolver. A read that
// started before a write must not put its stale answer back after the invalidation.
public class DeviceSettingsCacheTests
{
  /// <summary>A settings read whose answer is released by the test, after the invalidation.</summary>
  private sealed class HeldListInvoker : CallInvoker
  {
    private readonly List<Action<object>> _complete = [];

    public int Calls { get; private set; }

    public void Release(ListDeviceSettingsResponse response)
    {
      if (_complete.Count == 0)
        throw new InvalidOperationException("no settings read is waiting");
      _complete[^1](response);
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
      Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
      if (method.Name != "ListDeviceSettings")
        throw new NotSupportedException(method.Name);

      Calls += 1;
      var answer = new TaskCompletionSource<TResponse>();
      _complete.Add((response) => answer.TrySetResult((TResponse)response));

      return new AsyncUnaryCall<TResponse>(
        answer.Task,
        Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess,
        () => new Metadata(),
        () => { });
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
      Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
      throw new NotSupportedException();

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
      Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
      throw new NotSupportedException();

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
      Method<TRequest, TResponse> method, string? host, CallOptions options) =>
      throw new NotSupportedException();

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
      Method<TRequest, TResponse> method, string? host, CallOptions options) =>
      throw new NotSupportedException();
  }

  [Fact]
  public async Task AReadThatFinishesAfterInvalidateIsNotCached()
  {
    var invoker = new HeldListInvoker();
    var cache = new DeviceSettingsCache(
      new CfgService.CfgServiceClient(invoker),
      new DaprClientBuilder().Build(),
      "statestore",
      NullLogger<DeviceSettingsCache>.Instance);

    // No sidecar in a unit test: the version check is the one thing this test does not exercise.
    typeof(DeviceSettingsCache)
      .GetField("_lastVersionCheck", BindingFlags.NonPublic | BindingFlags.Instance)!
      .SetValue(cache, DateTime.UtcNow);

    var first = cache.ListAsync("camera-1", CancellationToken.None);
    cache.Invalidate("camera-1");
    invoker.Release(new ListDeviceSettingsResponse());
    await first;

    var second = cache.ListAsync("camera-1", CancellationToken.None);
    invoker.Release(new ListDeviceSettingsResponse());
    await second;

    // The stale answer was not cached: the second read asked VmsCfg again instead of reusing it.
    Assert.Equal(2, invoker.Calls);
  }
}
