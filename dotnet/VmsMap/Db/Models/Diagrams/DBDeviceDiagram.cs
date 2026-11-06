using System;

namespace VmsMap.Db
{
  // A diagram the map maintains from a template for one real device. The row's existence is the
  // whole meaning: a diagram in this table has its replicas kept in step with the template, one
  // outside it is an ordinary mnemoscheme nobody touches.
  //
  // `object_id` is derived from (source_kind, source_id, slot) and never from the type, so a device
  // that starts reporting another model keeps its node and merely swaps what is drawn on it.
  public record DBDeviceDiagram
  {
    public Guid object_id { get; set; }
    public string source_kind { get; set; } = null!;
    public string source_id { get; set; } = null!;
    public string slot { get; set; } = "default";

    // Null once the type has been deleted: the canvas and its replicas stay, drawn by nothing.
    public Guid? diagram_type_id { get; set; }

    // Who asked for it, not how it behaves: an attached diagram is maintained exactly like an
    // automatic one.
    public bool auto_created { get; set; }
  }

  // The replicas the service made for one diagram. Only these are moved, renamed or removed by
  // reconciliation; a replica an administrator drew by hand is not in this table and is never
  // touched.
  public record DBDeviceDiagramPart
  {
    public Guid diagram_object_id { get; set; }
    public string region_key { get; set; } = null!;
    public Guid part_object_id { get; set; }
    public Guid replica_object_id { get; set; }
  }
}
