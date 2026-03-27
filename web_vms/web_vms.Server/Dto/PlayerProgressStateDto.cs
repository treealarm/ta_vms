namespace web_vms.Server.Dto
{
  public record PlayerProgressStateDto
  {
    public string camera_id { get; init; } = default!;
    public string client_id { get; init; } = default!;
    public DateTime? position_ts { get; set; }
    public string? file_path { get; init; } = default!;
    public DateTime? start_ts { get; set; }
    public uint duration_ms { get; set; } = 0;
    public uint position_ms { get; set; } = 0;
  }
}
