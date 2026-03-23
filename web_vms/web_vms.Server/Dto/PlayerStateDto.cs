namespace web_vms
{
  public record PlayerStateDto
  {
    public string CameraId { get; init; } = default!;
    public string ClientId { get; init; } = default!;
    public DateTime? Position { get; set; } // если нужно менять
    public string? FilePath { get; init; } = default!;
  }
}
