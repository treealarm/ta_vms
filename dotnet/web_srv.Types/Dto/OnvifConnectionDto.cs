namespace web_srv.Types.Dto
{
  public record OnvifConnectionDto
  {
    required public string ip { get; set; }
    required public int port { get; set; }
    public string? xaddr { get; set; }
  }
}
