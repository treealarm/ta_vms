using System;
using System.Text.Json;

namespace VmsMap.Db
{
  public record DBValue : BasePgEntity
  {
    public Guid owner_id { get; set; }

    public string name { get; set; } = null!;

    public JsonDocument? value { get; set; }
  }
}
