namespace web_vms;

public static class HttpResponseExtensions
{
  public static Task WriteErrorAsync(this HttpResponse response, int statusCode, string message, CancellationToken ct)
  {
    response.StatusCode = statusCode;
    return response.WriteAsJsonAsync(new { error = message }, ct);
  }
}
