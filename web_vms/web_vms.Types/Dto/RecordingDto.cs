namespace web_vms.Types.Dto
{
  public class RecordingsQueryDto
  {
    public string CameraId { get; set; } = "cam1";
    public DateTime? Timestamp { get; set; } = DateTime.UtcNow;
    public uint? Limit { get; set; } = 50;
  }

  public class RecordingDto
  {
    public string FilePath { get; set; } = default!;
    public DateTime StartTime { get; set; }
  }

  public class RecordingsResponseDto
  {
    public List<RecordingDto> Recordings { get; set; } = [];
    public RecordingDto? Nearest { get; set; }
  }
}
