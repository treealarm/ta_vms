namespace web_vms.Types.Dto;

public class EntityStateDto
{
  public string Service { get; set; } = string.Empty;
  public string EntityType { get; set; } = string.Empty;
  public string EntityId { get; set; } = string.Empty;
  public string StateJson { get; set; } = string.Empty;
  public DateTime LastSeen { get; set; }
  public bool Stale { get; set; }
}
