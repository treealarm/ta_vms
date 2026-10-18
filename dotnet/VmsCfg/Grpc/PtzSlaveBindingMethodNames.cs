using Vms.Cfg;

namespace VmsCfg;

/// <summary>
/// The words for <see cref="PtzSlaveBindingMethod"/>: the <c>ptz_slave_binding.method</c> column and
/// the REST DTO use the same ones, so there is one table for both.
/// </summary>
public static class PtzSlaveBindingMethodNames
{
  private static readonly (PtzSlaveBindingMethod Method, string Name)[] Names =
  [
    (PtzSlaveBindingMethod.PtzSlaveBindingAbsolute, "absolute"),
    (PtzSlaveBindingMethod.PtzSlaveBindingPresets, "presets"),
    (PtzSlaveBindingMethod.PtzSlaveBindingGeo, "geo"),
  ];

  public static string ToName(PtzSlaveBindingMethod method) =>
    Names.First(n => n.Method == method).Name;

  /// <returns>The method named; "absolute", the column's default, for a name it does not know.</returns>
  public static PtzSlaveBindingMethod FromName(string name)
  {
    foreach (var (method, known) in Names)
      if (known.Equals(name, StringComparison.OrdinalIgnoreCase))
        return method;
    return PtzSlaveBindingMethod.PtzSlaveBindingAbsolute;
  }
}
