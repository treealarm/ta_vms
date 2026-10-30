namespace VmsMap.Db
{
  public record DBObjectStateDescription : BasePgEntity
  {
    public bool alarm { get; set; }
    public string state { get; set; } = null!;
    public string? state_descr { get; set; }
    public string? state_color { get; set; }
  }
}
