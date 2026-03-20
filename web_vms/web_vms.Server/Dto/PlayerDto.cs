namespace web_vms
{
  public record PlayerDto
  {
    public string CameraId { get; init; } = default!;
    public string ClientId { get; init; } = default!;
    public DateTime StartTime { get; init; }
  }
}
