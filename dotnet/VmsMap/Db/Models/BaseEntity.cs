using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VmsMap.Db
{
  // Every stored row carries the database's xmin as Version: a poller can then tell that a row
  // changed without a column of our own, which is how M3's Watch will stream updates.
  public abstract record BasePgEntity
  {
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid id { get; set; }

    [Timestamp]
    public uint Version { get; private set; }
  }
}
