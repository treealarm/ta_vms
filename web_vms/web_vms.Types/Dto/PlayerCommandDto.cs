namespace web_vms.Types.Dto
{
  public record PlayerCommandDto
  {
    public string client_id { get; init; } = default!;
    public string command { get; init; } = default!;
    public int? step_frames { get; init; }
    public double? speed { get; init; }
  }
}
