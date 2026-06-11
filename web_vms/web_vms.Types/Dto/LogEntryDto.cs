namespace web_vms.Types.Dto;

public class LogEntryDto
{
  public DateTime Timestamp { get; set; }
  public string Level { get; set; } = string.Empty;
  public string AppId { get; set; } = string.Empty;
  public string File { get; set; } = string.Empty;
  public string Function { get; set; } = string.Empty;
  public string Message { get; set; } = string.Empty;
}
