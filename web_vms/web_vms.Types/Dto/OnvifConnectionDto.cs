namespace web_vms.Types.Dto
{
  public record OnvifConnectionDto
  {
    required public string ip { get; set; }
    required public int port { get; set; }
  }
}
