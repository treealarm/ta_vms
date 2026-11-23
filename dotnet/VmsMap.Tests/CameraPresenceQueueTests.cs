using VmsMap.Adapter;

namespace VmsMap.Tests;

// The queue collapses a burst of events for one camera into one evaluation: a media-server restart
// that flips a thousand streams must not become a thousand reads and writes.
public sealed class CameraPresenceQueueTests
{
  private const string FirstCamera = "cam1";
  private const string SecondCamera = "cam2";

  [Fact]
  public void EnqueuingTheSameCameraTwiceTakesItOnce()
  {
    var queue = new CameraPresenceQueue();

    queue.Enqueue(FirstCamera);
    queue.Enqueue(FirstCamera);
    queue.Enqueue(SecondCamera);

    var batch = queue.TakeAll();
    Assert.Equal(2, batch.Count);
    Assert.Contains(FirstCamera, batch);
    Assert.Contains(SecondCamera, batch);
    Assert.Empty(queue.TakeAll());
  }

  [Fact]
  public void ACameraEnqueuedAfterTheDrainIsTakenNextTime()
  {
    var queue = new CameraPresenceQueue();
    queue.Enqueue(FirstCamera);
    Assert.Equal(new[] { FirstCamera }, queue.TakeAll());

    queue.Enqueue(FirstCamera);
    Assert.Equal(new[] { FirstCamera }, queue.TakeAll());
  }

  [Fact]
  public void BlankIdsAreIgnored()
  {
    var queue = new CameraPresenceQueue();

    queue.Enqueue(null);
    queue.Enqueue(string.Empty);
    queue.Enqueue("  ");

    Assert.Empty(queue.TakeAll());
  }
}
