namespace web_srv.Types.Dto
{
  public record CredentialsDto
  {
    public string? User { get; init; }
    public string? Pass { get; init; }
  }
}
