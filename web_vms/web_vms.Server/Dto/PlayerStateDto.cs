namespace web_vms
{
  public record PlayerStateDto
  {
    public string camera_id { get; init; } = default!;
    public string client_id { get; init; } = default!;
    public DateTime? position_ts { get; set; } // если нужно менять
  }
}
