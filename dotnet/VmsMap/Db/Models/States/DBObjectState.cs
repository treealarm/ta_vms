using System;
using System.Collections.Generic;

namespace VmsMap.Db
{
  public record DBObjectStateValue : BasePgEntity
  {
    // The monitored object; the same value as its DBObjectState.id.
    public Guid object_id { get; set; }
    public string state { get; set; } = null!;
  }

  public record DBObjectState : BasePgEntity
  {
    public DateTime timestamp { get; set; }

    public List<DBObjectStateValue> states { get; set; } = new();
  }
}
