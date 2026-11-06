using System;

namespace VmsMap.Db
{
  // The registry from a real device or one of its parts to the tree node that stands for it. The
  // node id is a one-way MD5 of the key, so this is the only way back; `source_id` is the device's
  // id in the owning service (a camera id today).
  //
  // A device as a whole is the row with part_kind='device' and an empty token; its parts are rows
  // of their own, so a fire panel's zones and relays can join the same registry later.
  public record DBDeviceNode
  {
    public Guid object_id { get; set; }
    public string source_kind { get; set; } = null!;
    public string source_id { get; set; } = null!;
    public string part_kind { get; set; } = null!;
    public string part_token { get; set; } = "";
  }
}
