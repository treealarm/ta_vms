using System;
using System.Collections.Generic;

namespace VmsMap.Domain
{
  public record ObjectStateDTO: BaseObjectDTO
  {
    public List<string>? states { get; set; }
    public DateTime? timestamp { get; set; }
  }
}
