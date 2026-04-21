namespace web_vms.Types.Dto;

public class LogResponseDto
{
  public List<LogEntryDto> Logs { get; set; } = [];
  public bool HasMore { get; set; }
}
