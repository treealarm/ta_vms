using System;
using System.Collections.Generic;
using System.Linq;

namespace VmsMap.Domain
{
  /// <summary>One configured part of a device: what it is, and what the operator called it.</summary>
  public sealed record DevicePartRef(string Kind, string Token, string Name);

  /// <summary>A template's region and the rule that is supposed to find its part.</summary>
  public sealed record RegionPartRule(string RegionKey, string PartKind, string MatchBy, string MatchValue);

  /// <summary>One region and the configured part it was found to stand for.</summary>
  public sealed record BoundRegion(string RegionKey, DevicePartRef Part);

  /// <summary>
  /// What the rules resolved to. <c>Unbound</c> is not a failure: a region may be a drawing or a
  /// caption, and so may a rule that found nothing on this particular device.
  /// </summary>
  public sealed record DiagramBindingResult(
    IReadOnlyList<BoundRegion> Bound,
    IReadOnlyList<string> Warnings);

  /// <summary>
  /// Turns a template's region rules into actual parts of one device.
  ///
  /// Each rule uses exactly one way of finding its part — the token, the configured name, or the
  /// 1-based position among parts of its kind once sorted by token. There is no cascade between
  /// them: a rule that matched by three would mean whatever the order of the checks happened to
  /// be, and the editor would have no way to say what it meant.
  ///
  /// A rule that matches nothing binds nothing. A rule that matches several (two parts sharing a
  /// name) binds nothing and says so. Two regions that end up on one part are a configuration
  /// conflict: neither is bound, because which region would show it depends on an order nobody
  /// reads as an order.
  /// </summary>
  public static class DiagramTemplateBinding
  {
    public static DiagramBindingResult Bind(
      IReadOnlyList<RegionPartRule> rules, IReadOnlyList<DevicePartRef> parts)
    {
      var warnings = new List<string>();
      var tentative = new List<BoundRegion>();

      foreach (var rule in rules)
      {
        var ofKind = parts
          .Where(p => string.Equals(p.Kind, rule.PartKind, StringComparison.Ordinal))
          .ToList();

        var found = Candidates(ofKind, rule, warnings);

        if (found.Count > 1)
          warnings.Add(
            $"Region '{rule.RegionKey}' matches {found.Count} parts by " +
            $"'{rule.MatchBy} = {rule.MatchValue}'; none of them is bound.");
        else if (found.Count == 1)
          tentative.Add(new BoundRegion(rule.RegionKey, found[0]));
      }

      var claimed = tentative
        .GroupBy(b => (b.Part.Kind, b.Part.Token))
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)
        .ToHashSet();

      foreach (var conflict in claimed)
        warnings.Add(
          $"More than one region claims {conflict.Kind} '{conflict.Token}'; none of them is bound.");

      return new DiagramBindingResult(
        tentative.Where(b => !claimed.Contains((b.Part.Kind, b.Part.Token))).ToList(),
        warnings);
    }

    private static List<DevicePartRef> Candidates(
      IReadOnlyList<DevicePartRef> ofKind, RegionPartRule rule, List<string> warnings)
    {
      switch (rule.MatchBy)
      {
        case DiagramPartMatch.Token:
          return ofKind
            .Where(p => string.Equals(p.Token, rule.MatchValue, StringComparison.Ordinal))
            .ToList();

        case DiagramPartMatch.Name:
          // The name is the operator's, and it may be empty. An empty name matches nothing rather
          // than everything: a rule that binds every unnamed relay is not what anybody wrote it
          // for.
          var wanted = rule.MatchValue.Trim();
          return wanted.Length == 0
            ? []
            : ofKind
                .Where(p => string.Equals(p.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();

        case DiagramPartMatch.Index:
          // The position is not a promise the camera makes: its list may be reordered by a
          // firmware update. Sorted by token it is at least stable between two snapshots of the
          // same device, which is what the rule needs to mean anything.
          if (!int.TryParse(rule.MatchValue, out var index) || index < 1)
          {
            warnings.Add(
              $"Region '{rule.RegionKey}' matches by index '{rule.MatchValue}', which is not a position.");
            return [];
          }

          var ordered = ofKind.OrderBy(p => p.Token, StringComparer.Ordinal).ToList();
          return index <= ordered.Count ? [ordered[index - 1]] : [];

        default:
          warnings.Add(
            $"Region '{rule.RegionKey}' matches by '{rule.MatchBy}', which is not a way of matching.");
          return [];
      }
    }
  }
}
