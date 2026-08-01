namespace web_vms;

/// <summary>
/// A dependency this request needs has not become available yet (typically VmsCfg, or the database
/// behind it). Distinct from a plain failure: the answer is 503 plus the reason, so the operator
/// sees "config service is down" instead of a bare 500 or a page stuck on "connecting".
/// </summary>
public sealed class ServiceNotReadyException(string message, string? detail = null) : Exception(message)
{
  /// <summary>Underlying error from the dependency, when we have one.</summary>
  public string? Detail { get; } = detail;
}
