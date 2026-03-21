namespace web_vms
{
  public record CameraStateDto
  {
    required public string CameraId { get; init; }
    public bool Recording { get; init; }
    public bool WebRtc { get; init; }
    public string? RtspUrl { get; init; }
  }
}
