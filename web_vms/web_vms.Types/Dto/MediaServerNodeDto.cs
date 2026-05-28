namespace web_vms.Types.Dto
{
  public record MediaServerNodeDto
  {
    required public string ServerId { get; init; }
    public string Status { get; init; } = "online";
    public string? FsId { get; init; }
  }
}
