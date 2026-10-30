namespace VmsMap.Domain
{
  public record ValueDTO: BaseObjectDTO
  {
    
    public string? owner_id { get; set; }// the object that owns this value
    public string? name { get; set; }
    public object? value { get; set; }
  }
}
