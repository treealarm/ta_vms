namespace web_srv;

public sealed class HeadRequestMiddleware(RequestDelegate next)
{
  public async Task InvokeAsync(HttpContext ctx)
  {
    if (!HttpMethods.IsHead(ctx.Request.Method))
    {
      await next(ctx);
      return;
    }

    var original = ctx.Response.Body;
    ctx.Response.Body = new NullWriteStream(original);
    try
    {
      await next(ctx);
    }
    finally
    {
      ctx.Response.Body = original;
    }
  }

  // Discards body writes, but delegates Flush so response headers are committed.
  private sealed class NullWriteStream(Stream inner) : Stream
  {
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;
    public override long Position { get => 0; set { } }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

    public override void Write(byte[] buffer, int offset, int count) { }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.CompletedTask;
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) => default;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
  }
}
