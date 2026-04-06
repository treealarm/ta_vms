using System.Text.Json;

namespace web_vms
{
  public class CameraStorage
  {
    private readonly string _filePath = "cameras.json";
    private readonly object _lock = new();

    public List<CameraStateDto> Load()
    {
      if (!File.Exists(_filePath))
        return new();

      var json = File.ReadAllText(_filePath);
      return JsonSerializer.Deserialize<List<CameraStateDto>>(json)
             ?? new();
    }

    public void Save(IEnumerable<CameraStateDto> cameras)
    {
      lock (_lock)
      {
        var json = JsonSerializer.Serialize(cameras, new JsonSerializerOptions
        {
          WriteIndented = true
        });

        File.WriteAllText(_filePath, json);
      }
    }
  }
}
