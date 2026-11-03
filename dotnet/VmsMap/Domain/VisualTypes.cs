
namespace VmsMap.Domain
{
  // How an abstract property's value is to be read and edited, stored in `visual_type`. The map
  // editor offers exactly these, and an empty type is plain text as well. Square's
  // action-parameter types -- password, coordinates, IP range, credential and enum lists,
  // snapshot, map -- went with the actions themselves and are not carried over.
  public static class VisualTypes
  {
    public const string Text = "__txt";
    public const string Color = "__clr";
    public const string DateTime = "__datetime";
    public const string Double = "__double";
    public const string Int = "__int";
  }
}
