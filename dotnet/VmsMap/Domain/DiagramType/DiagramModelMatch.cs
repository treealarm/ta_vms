using System;
using System.Collections.Generic;
using System.Linq;

namespace VmsMap.Domain
{
  /// <summary>One type as the matcher sees it: what it is for, and whether it attaches by itself.</summary>
  public sealed record DiagramTypeCandidate(
    string Id,
    string? SourceKind,
    string Manufacturer,
    string Model,
    bool AutoAttach);

  /// <summary>
  /// The type chosen for one device, or why nothing was: a tie is not resolved. An arbitrary pick
  /// would draw the wrong panel and look like a correct answer.
  /// </summary>
  public sealed record DiagramModelSelection(string? TypeId, string? Ambiguity)
  {
    public bool IsMatch => TypeId is not null;
  }

  /// <summary>
  /// Which auto-attaching template a device gets, if any.
  ///
  /// Only types that ask to attach themselves are candidates, and only for their own source kind.
  /// An empty manufacturer or model is a wildcard, and the most specific type wins — the number of
  /// constraints it actually states. Two equally specific candidates are an ambiguity: nothing is
  /// created and the caller says so, because a device drawn from the wrong template is worse than
  /// one with no diagram at all.
  /// </summary>
  public static class DiagramModelMatch
  {
    public static DiagramModelSelection Select(
      IEnumerable<DiagramTypeCandidate> candidates,
      string sourceKind,
      string? manufacturer,
      string? model)
    {
      var matches = candidates
        .Where(c => c.AutoAttach)
        .Where(c => Matches(c, sourceKind, manufacturer, model))
        .ToList();

      if (matches.Count == 0)
        return new DiagramModelSelection(null, null);

      var most = matches.Max(Specificity);
      var best = matches.Where(c => Specificity(c) == most).ToList();

      if (best.Count == 1)
        return new DiagramModelSelection(best[0].Id, null);

      return new DiagramModelSelection(null,
        $"Types {string.Join(", ", best.Select(c => $"\"{c.Id}\""))} all match " +
        $"{sourceKind} '{manufacturer ?? ""}'/'{model ?? ""}' equally well; none was chosen.");
    }

    /// <summary>
    /// Whether one type describes this device, whatever its <c>auto_attach</c>: a type an
    /// administrator attached by hand keeps its diagram as long as its model still fits.
    /// </summary>
    public static bool Matches(
      DiagramTypeCandidate candidate, string sourceKind, string? manufacturer, string? model) =>
      string.Equals(candidate.SourceKind, sourceKind, StringComparison.Ordinal)
      && Matches(candidate.Manufacturer, manufacturer)
      && Matches(candidate.Model, model);

    private static int Specificity(DiagramTypeCandidate candidate) =>
      (IsSet(candidate.Manufacturer) ? 1 : 0) + (IsSet(candidate.Model) ? 1 : 0);

    private static bool Matches(string templateValue, string? actual) =>
      !IsSet(templateValue)
      || string.Equals(templateValue.Trim(), actual?.Trim() ?? string.Empty,
                       StringComparison.OrdinalIgnoreCase);

    private static bool IsSet(string? value) => !string.IsNullOrWhiteSpace(value);
  }
}
