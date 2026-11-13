using System;

namespace VmsMap.Db
{
  /// <summary>
  /// An explicit arming decision on a node: a row says this space (or zone) is armed or disarmed
  /// here, and no row says it follows whoever is above it. It is the map's own state, so it is a
  /// typed row rather than one of the object's free-form properties — a property would be edited
  /// on the object's form, without the journal entry and the alarm re-check an arming change owes.
  /// </summary>
  public class DBObjectArming
  {
    public Guid object_id { get; set; }
    public bool armed { get; set; }
  }
}
