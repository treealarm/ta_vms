namespace web_vms.Types.Dto;

public class LogQueryDto
{
  public DateTime? From { get; set; }
  public DateTime? To { get; set; }
  public uint? Limit { get; set; }
  public string? Level { get; set; }
  public string? Source { get; set; }
  public string? AppId { get; set; }
}
