namespace web_vms
{
  public record PlayerStateDto
  {
    public string CameraId { get; init; } = default!;
    public string ClientId { get; init; } = default!;
    public string? StartTime { get; init; } // если нужно менять
  }
}
