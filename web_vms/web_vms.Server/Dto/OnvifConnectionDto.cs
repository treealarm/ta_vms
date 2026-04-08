namespace web_vms
{
  public record OnvifConnectionDto
  {
    required public string ip { get; set; }
    required public int port { get; set; }
  }
}
