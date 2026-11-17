using System;
using System.Collections.Generic;
using System.Globalization;

namespace VmsMap.Domain
{
  /// The name a replica gets when nobody names it: its owner's name and its own number among the
  /// owner's replicas — `cam1 rep1`, `relay4 rep2`. A name is a type and a number, and a picture
  /// says which node it is a picture of, because states, values, the node's kind and rights all
  /// resolve an owner in one hop.
  public static class ReplicaNames
  {
    /// What separates the owner's name from the replica's number.
    public const string Separator = " rep";

    /// The next free name among an owner's replicas. The number is the one after the highest in
    /// use, not the count of replicas: a removal in the middle must not let two pictures of one
    /// node carry the same name.
    public static string Next(string ownerName, IEnumerable<string?> existing)
    {
      var prefix = ownerName + Separator;
      var next = 1;

      foreach (var name in existing)
      {
        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
        if (!int.TryParse(name[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture,
              out var number))
          continue;
        if (number >= next) next = number + 1;
      }

      return prefix + next.ToString(CultureInfo.InvariantCulture);
    }
  }
}
