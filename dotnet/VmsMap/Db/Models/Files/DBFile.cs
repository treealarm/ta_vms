using System;

namespace VmsMap.Db
{
  // A file in the database rather than on a volume: the service is replicable, so a path would
  // tie the row to the instance that wrote it. Plan pictures, diagram backgrounds and event
  // crops are the rows.
  public record DBFile
  {
    public Guid id { get; set; }
    public string mime { get; set; } = null!;
    public byte[] bytes { get; set; } = null!;
    public string? etag { get; set; }
  }
}
