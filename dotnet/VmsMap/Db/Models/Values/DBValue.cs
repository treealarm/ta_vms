using System;


namespace VmsMap.Db
{
  public record DBValue : BasePgEntity
  {
    public Guid owner_id { get; set; }

    public string name { get; set; } = null!;

    public string? value { get; set; }
  }
}
