namespace web_vms;

public sealed record TimelineSlice(DateTime Start, DateTime End)
{
  public TimeSpan Duration => End - Start;
  public bool Contains(DateTime dt) => dt >= Start && dt <= End;
  public double OffsetOf(DateTime dt) => (dt - Start).TotalSeconds;
}
