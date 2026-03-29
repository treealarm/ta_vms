namespace web_vms.Server.Dto
{
  public record PlayerCommandDto
  {
    public string client_id { get; init; } = default!;
    public string command { get; init; } = default!;
  }
}
