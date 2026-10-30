namespace VmsMap.Db
{
  // The single row that decides what a node with no rights rows means. It lived in VmsCfg's
  // camera_acl_setting while rights did; rights are the tree's now, so the switch follows them.
  public record DBRightsMode
  {
    public bool id { get; set; } = true;
    public string mode { get; set; } = null!;
  }
}
