using System;

namespace VmsMap.Db
{
  // One installed demo world: its version and when it was last written. The rows of the world
  // itself are in demo_object, which is what an install compares its model against.
  public class DBDemoConfig
  {
    public string demo_key { get; set; } = string.Empty;
    public int version { get; set; }
    public DateTime applied_at { get; set; }
  }

  // A row the demo owns: the logical key is the world model's name for it, the id is what it was
  // created as here (a space gets its id from the server, everything else from DemoIds). The
  // prefix of logical_key is its kind -- space, diagram, type, file, level, camera.
  public class DBDemoObject
  {
    public string demo_key { get; set; } = string.Empty;
    public string logical_key { get; set; } = string.Empty;
    public Guid object_id { get; set; }
  }
}
